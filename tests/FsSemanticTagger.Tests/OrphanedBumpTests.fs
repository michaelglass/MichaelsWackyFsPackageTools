/// A release must never leave a package's `<Version>` bumped without publishing it.
///
/// The shape these tests pin is an FsHotWatch release. Its bump commit moved three
/// packages — the core library, a plugin that depends on it and a CLI that depends on
/// both — and tagged all three locally. The wait for CI on that commit timed out before
/// any tag was pushed, and the re-run that resumed the release pushed the plugin's and
/// the CLI's tags but not core's: core had no changes since its (local, never pushed)
/// tag, and the feed did not answer a definite "absent" for it, so core was skipped as
/// "already released". The published plugin then named a core version that was never
/// on NuGet, and the next release could not read core's "previous release" at all.
module FsSemanticTagger.Tests.OrphanedBumpTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Api
open FsSemanticTagger.Release
open Tests.Common.TestHelpers
open FsSemanticTagger.Tests.ExtractionFakes

let private noCanary: ConsumerCanary.Settings =
    {
        ConfigPath = Path.Combine(Path.GetTempPath(), "no-such-fssemantictagger.json")
        Skip = false
        LogDir = Path.Combine(Path.GetTempPath(), "fssemantictagger-canary-logs")
        PackagesCache = Path.Combine(Path.GetTempPath(), "fssemantictagger-canary-cache")
        Ops =
            {
                RunIn = fun _ cmd _ -> failwithf "unexpected canary process: %s" cmd
                RunGate = fun _ command _ _ -> failwithf "unexpected canary gate: %s" command
            }
    }

/// One package of the fixture repo: its name, the packages it references, the version
/// its fsproj declares, and its tag prefix.
type private Pkg =
    {
        Name: string
        Refs: string list
        Declared: string
        Prefix: string
    }

/// The repository state a release run observes.
type private World =
    {
        /// Tags in the local repo.
        LocalTags: string list
        /// Tags on the remote. `None` when the remote cannot be listed.
        RemoteTags: string list option
        /// Names of packages whose source changed since their newest tag.
        Changed: string list
    }

let private core =
    {
        Name = "Core"
        Refs = []
        Declared = "0.10.0-alpha.55"
        Prefix = "core-v"
    }

let private cli =
    {
        Name = "Cli"
        Refs = [ "Core"; "TestPrune" ]
        Declared = "0.14.0-alpha.73"
        Prefix = "cli-v"
    }

let private testPrune =
    {
        Name = "TestPrune"
        Refs = [ "Core" ]
        Declared = "0.13.0-alpha.54"
        Prefix = "testprune-v"
    }

/// Listed as FsHotWatch lists them: core, then the CLI, then the plugin.
let private packages = [ core; cli; testPrune ]

let private writePackage (root: string) (pkg: Pkg) : PackageConfig =
    let dir = Path.Combine(root, "src", pkg.Name)
    Directory.CreateDirectory(dir) |> ignore

    let references =
        pkg.Refs
        |> List.map (fun r -> sprintf "<ProjectReference Include=\"../%s/%s.fsproj\" />" r r)
        |> String.concat ""

    File.WriteAllText(
        Path.Combine(dir, pkg.Name + ".fsproj"),
        sprintf
            "<Project><PropertyGroup><Version>%s</Version></PropertyGroup><ItemGroup>%s</ItemGroup></Project>"
            pkg.Declared
            references
    )

    File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- feat: a change\n")

    {
        Name = pkg.Name
        Fsproj = Path.Combine(dir, pkg.Name + ".fsproj")
        DllPath = Path.Combine(dir, "bin", pkg.Name + ".dll")
        TagPrefix = pkg.Prefix
        FsProjsSharingSameTag = []
    }

/// A remote on which CI is green and every pushed tag has a green publish run. Tag
/// pushes and writes land on the timeline; tags come from `world`.
let private fakeRun (root: string) (world: World) (timeline: ResizeArray<string>) (cmd: string) (args: string) =
    let tagsWithPrefix (prefix: string) =
        world.LocalTags |> List.filter (fun t -> t.StartsWith prefix)

    match cmd, args with
    | "jj", "diff --summary" -> Success ""
    | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
    | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
    | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
    | "gh", a when a.Contains("run list") ->
        Success """[{"status":"completed","conclusion":"success","name":"Release","url":"https://example.com/1"}]"""
    | "dotnet", "build -c Release" -> Success "Build succeeded."
    | "git", a when a.StartsWith("tag -l \"") ->
        let prefix = a.Substring("tag -l \"".Length).TrimEnd('"').TrimEnd('*')
        Success(tagsWithPrefix prefix |> String.concat "\n")
    | "jj", a when a.StartsWith("tag list ") && not (a.Contains "glob:") ->
        let tag = a.Substring("tag list ".Length)
        Success(if List.contains tag world.LocalTags then tag else "")
    | "git", a when a.StartsWith("ls-remote") ->
        match world.RemoteTags with
        | Some tags ->
            tags
            |> List.map (fun t -> sprintf "0123456789abcdef\trefs/tags/%s" t)
            |> String.concat "\n"
            |> Success
        | None -> Failure("fatal: unable to access the remote", 128)
    | "jj", a when a.StartsWith("diff --from ") ->
        let changed =
            world.Changed
            |> List.exists (fun name -> a.Contains(Path.Combine(root, "src", name) + "/**"))

        Success(if changed then "M src/changed.fs" else "")
    | "jj", a when a.StartsWith("git push --tag ") ->
        timeline.Add(sprintf "push %s" (a.Substring("git push --tag ".Length)))
        Success ""
    | "jj", a when
        a.StartsWith("tag set")
        || a.StartsWith("commit")
        || a.StartsWith("bookmark set")
        || a = "git push"
        || a = "git export"
        ->
        timeline.Add(sprintf "write %s" (a.Split(' ')[0]))
        Success ""
    | "git", _ -> Failure("not a git repo", 1)
    | "jj", a when a.StartsWith("tag list ") -> Failure("use git", 1)
    | "jj", a when a.StartsWith("log -r ") -> Failure("no such revision", 1)
    | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

let private run
    (world: World)
    (command: ReleaseCommand)
    (targets: string list)
    (checkFeed: string -> string -> FeedPresence)
    : string * int * string list =
    withTempDir (fun root ->
        let config =
            {
                Packages = packages |> List.map (writePackage root)
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let timeline = ResizeArray<string>()

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun root world timeline
                        Config = config
                        Command = command
                        Mode = PushTags
                        TargetPackages = targets
                        ExtractPrevious = noPrevious
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = noCurrent
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush =
                            {
                                PushAttempts = 1
                                PushRetryDelayMs = 0
                                RunPollIntervalMs = 0
                                RunPollAttempts = 1
                            }
                        CheckFeedPresence = checkFeed
                        CheckRestorable = fun _ _ _ -> OnFeed
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        output, result, List.ofSeq timeline)

let private tagOf (pkg: Pkg) = pkg.Prefix + pkg.Declared

/// The tags each package had before the release being resumed.
let private previousTags =
    [
        "core-v0.10.0-alpha.54"
        "cli-v0.14.0-alpha.72"
        "testprune-v0.13.0-alpha.53"
    ]

let private pushes (timeline: string list) =
    timeline |> List.filter (fun e -> e.StartsWith "push ")

let private writes (timeline: string list) =
    timeline |> List.filter (fun e -> e.StartsWith "write ")

/// The feed as it answered on the re-run: the plugin's and the CLI's new versions are
/// definitely absent, and core's is not reported absent — `core` is what it said.
let private feedSaying (core: FeedPresence) (id: string) (version: string) =
    match id, version with
    | "Core", "0.10.0-alpha.55" -> core
    | _, v when v = cli.Declared || v = testPrune.Declared -> NotOnFeed
    | _ -> OnFeed

/// REGRESSION: the re-run of an FsHotWatch release whose CI wait timed out after every
/// tag was created locally and before any was pushed. Every bumped package must be
/// published, core first — whatever the feed says about core.
[<Theory>]
[<InlineData("unknown")>]
[<InlineData("listed")>]
let ``resuming a release whose tags were never pushed pushes the bumped dependency first`` (coreOnFeed: string) =
    let coreAnswer =
        match coreOnFeed with
        | "listed" -> OnFeed
        | _ -> FeedUnknown "The operation has timed out."

    let world =
        {
            LocalTags = previousTags @ (packages |> List.map tagOf)
            RemoteTags = Some previousTags
            Changed = []
        }

    let output, result, timeline = run world Auto [] (feedSaying coreAnswer)

    test <@ result = 0 @>

    test
        <@
            pushes timeline =
                [
                    "push core-v0.10.0-alpha.55"
                    "push testprune-v0.13.0-alpha.54"
                    "push cli-v0.14.0-alpha.73"
                ]
        @>

    test <@ output.Contains "core-v0.10.0-alpha.55 exists locally but was never pushed" @>

/// Positive control for the regression above: a release that really is finished (every
/// tag on the remote, every version on the feed) is still a no-op.
[<Fact>]
let ``a finished release with every tag pushed and published does nothing`` () =
    let tags = previousTags @ (packages |> List.map tagOf)

    let world =
        {
            LocalTags = tags
            RemoteTags = Some tags
            Changed = []
        }

    let output, result, timeline = run world Auto [] (fun _ _ -> OnFeed)

    test <@ result = 0 @>
    test <@ output.Contains "No packages to release" @>
    test <@ List.isEmpty timeline @>

/// PREFLIGHT: the tree declares a core version that an earlier release bumped but never
/// published, and this release would move past it rather than finish it. Released
/// packages already name that version, so it is refused before anything is written or
/// pushed, naming the package, the version and the repair.
[<Theory>]
[<InlineData("tag never pushed")>]
[<InlineData("never tagged")>]
[<InlineData("tag pushed, never published")>]
let ``a declared version an earlier release never published refuses the release before any write`` (shape: string) =
    let coreTag = tagOf core

    let world, feed =
        match shape with
        | "tag never pushed" ->
            // The next release after the incident: core changed since its local tag.
            {
                LocalTags = previousTags @ (packages |> List.map tagOf)
                RemoteTags = Some(previousTags @ [ tagOf cli; tagOf testPrune ])
                Changed = [ "Core" ]
            },
            (fun _ _ -> OnFeed)
        | "never tagged" ->
            // The bump commit exists but core's tag was never created; releasing only
            // the plugin must not pass over it.
            {
                LocalTags = previousTags @ [ tagOf cli; tagOf testPrune ]
                RemoteTags = Some(previousTags @ [ tagOf cli; tagOf testPrune ])
                Changed = [ "TestPrune" ]
            },
            (fun _ _ -> OnFeed)
        | _ ->
            // The tag reached the remote but its publish never landed.
            {
                LocalTags = previousTags @ (packages |> List.map tagOf)
                RemoteTags = Some(previousTags @ (packages |> List.map tagOf))
                Changed = [ "Core" ]
            },
            (fun id version ->
                if id = "Core" && version = core.Declared then
                    NotOnFeed
                else
                    OnFeed)

    let targets = if shape = "never tagged" then [ "TestPrune" ] else []

    let output, result, timeline = run world StartAlpha targets feed

    test <@ result = 1 @>
    test <@ List.isEmpty (writes timeline) @>
    test <@ List.isEmpty (pushes timeline) @>
    test <@ output.Contains "Core 0.10.0-alpha.55" @>
    test <@ output.Contains coreTag @>
    test <@ output.Contains "Aborting before any writes" @>

/// Nothing in the repo depends on the CLI, so no published package can name its
/// missing version: the release warns and goes ahead, moving past it.
[<Fact>]
let ``an unpublished version nothing depends on is warned about, not refused`` () =
    let tags = previousTags @ (packages |> List.map tagOf)

    let world =
        {
            LocalTags = tags
            RemoteTags = Some tags
            Changed = [ "Cli" ]
        }

    let feed id version =
        if id = "Cli" && version = cli.Declared then
            NotOnFeed
        else
            OnFeed

    let output, result, timeline = run world StartAlpha [] feed

    test <@ result = 0 @>
    test <@ output.Contains "Warning: Cli 0.14.0-alpha.73 was never published" @>
    test <@ pushes timeline = [ "push cli-v0.15.0-alpha.1" ] @>

[<Fact>]
let ``remote tags are read from ls-remote, peeled annotated tags included once`` () =
    let output =
        "aaa\trefs/tags/core-v1.0.0\nbbb\trefs/tags/core-v1.0.0^{}\nccc\trefs/tags/cli-v2.0.0\nnot a ref line"

    test <@ Vcs.parseRemoteTags output = set [ "core-v1.0.0"; "cli-v2.0.0" ] @>

/// The release entry point publishes a separately released dependency before the
/// packages that depend on it. Each test records the tag pushes and feed checks in
/// one timeline, so "the dependent tag was not pushed early" is read off the order in
/// which things actually happened rather than inferred from the final exit code.
module FsSemanticTagger.Tests.ReleaseWavesTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Api
open FsSemanticTagger.Release
open Tests.Common.TestHelpers

type private Repo =
    { Root: string
      Config: ToolConfig
      Timeline: ResizeArray<string> }

/// Write one package (fsproj at 0.0.0 referencing `refs`, plus a changelog with an
/// Unreleased entry) under `root/src/<name>`.
let private writePackageWith (packAsTool: bool) (root: string) (name: string) (refs: string list) : PackageConfig =
    let dir = Path.Combine(root, "src", name)
    Directory.CreateDirectory(dir) |> ignore
    let tool = if packAsTool then "<PackAsTool>true</PackAsTool>" else ""

    let references =
        refs
        |> List.map (fun r -> sprintf "<ProjectReference Include=\"../%s/%s.fsproj\" />" r r)
        |> String.concat ""

    File.WriteAllText(
        Path.Combine(dir, name + ".fsproj"),
        sprintf
            "<Project><PropertyGroup><Version>0.0.0</Version>%s</PropertyGroup><ItemGroup>%s</ItemGroup></Project>"
            tool
            references
    )

    File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- feat: a change\n")

    { Name = name
      // Absolute: the release reads and rewrites `Fsproj` as given, not against RootDir.
      Fsproj = Path.Combine(dir, name + ".fsproj")
      DllPath = Path.Combine(dir, "bin", name + ".dll")
      TagPrefix = name.ToLowerInvariant() + "-v"
      FsProjsSharingSameTag = [] }

let private writePackage = writePackageWith false

/// Packages in config order, each with the packages it references. Names listed in
/// `tools` are written as `<PackAsTool>true</PackAsTool>` projects.
let private withRepoOf (tools: string list) (packages: (string * string list) list) (action: Repo -> unit) =
    withTempDir (fun root ->
        let config =
            { Packages =
                packages
                |> List.map (fun (name, refs) -> writePackageWith (List.contains name tools) root name refs)
              ReservedVersions = Set.empty
              PreBuildCmds = []
              PublishWorkflows = defaultPublishWorkflows
              RootDir = root }

        action
            { Root = root
              Config = config
              Timeline = ResizeArray() })

let private withRepo packages action = withRepoOf [] packages action

/// A remote on which CI is green, every tag push succeeds (except `rejectedTags`), and
/// every pushed tag has a green publish run. Tag pushes land on the timeline.
let private remote (timeline: ResizeArray<string>) (rejectedTags: string list) (cmd: string) (args: string) =
    match cmd, args with
    | "jj", "diff --summary" -> Success ""
    | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
    | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
    | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
    | "gh", a when a.Contains("run list") ->
        Success """[{"status":"completed","conclusion":"success","name":"Release","url":"https://example.com/1"}]"""
    | "dotnet", "build -c Release" -> Success "Build succeeded."
    | "git", a when a.StartsWith("tag -l") -> Success ""
    | "jj", a when a.StartsWith("git push --tag ") ->
        let tag = a.Substring("git push --tag ".Length)

        if List.contains tag rejectedTags then
            Failure("rejected", 1)
        else
            timeline.Add(sprintf "push %s" tag)
            Success ""
    | "git", a when a.StartsWith("push origin ") -> Failure("rejected", 1)
    | "git", _ -> Failure("not a git repo", 1)
    | "jj", a when
        a.StartsWith("tag set")
        || a.StartsWith("commit")
        || a.StartsWith("bookmark set")
        || a = "git push"
        || a = "git export"
        ->
        timeline.Add(sprintf "write %s" (a.Split(' ')[0]))
        Success ""
    | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

/// A feed on which each package appears only after it has been asked about
/// `checksUntilPublished` times (absent names never appear). Checks land on the timeline
/// under `label`, so the presence probe and the restorability probe can be told apart.
let private probe (label: string) (timeline: ResizeArray<string>) (checksUntilPublished: Map<string, int>) =
    let asked = System.Collections.Generic.Dictionary<string, int>()

    fun (id: string) (version: string) ->
        let count =
            match asked.TryGetValue id with
            | true, previous -> previous + 1
            | _ -> 1

        asked[id] <- count

        let presence =
            match checksUntilPublished.TryFind id with
            | Some needed when count >= needed -> OnFeed
            | _ -> NotOnFeed

        timeline.Add(sprintf "%s %s %s %A" label id version presence)
        presence

/// The presence probe (`CheckFeedPresence`): what the index says.
let private feed timeline checksUntilPublished =
    probe "feed" timeline checksUntilPublished

/// The restorability probe (`CheckRestorable`), which also records whether it was
/// asked about a tool package, since a tool has a different strongest answer.
let private restorable (timeline: ResizeArray<string>) (checksUntilPublished: Map<string, int>) =
    let inner = probe "restorable" timeline checksUntilPublished

    fun (isTool: bool) (id: string) (version: string) ->
        if isTool then
            timeline.Add(sprintf "restorable(tool) %s %s" id version)

        inner id version

let private releaseInputWith (repo: Repo) (rejectedTags: string list) checkFeed checkRestorable waitForNuGet mode =
    { Run = remote repo.Timeline rejectedTags
      Config = repo.Config
      Command = StartAlpha
      Mode = mode
      TargetPackages = []
      ExtractPreviousApi = fun _ _ -> FetchError "not used"
      ExtractCurrentApi = fun _ -> []
      ExtractPreviousGrammar = fun _ _ -> None
      ExtractCurrentGrammar = fun _ -> None
      CiPollIntervalMs = 0
      CiMaxAttempts = 10
      TagPush =
        { PushAttempts = 1
          PushRetryDelayMs = 0
          RunPollIntervalMs = 0
          RunPollAttempts = 1 }
      CheckFeedPresence = checkFeed
      CheckRestorable = checkRestorable
      WaitForNuGet = waitForNuGet
      NuGetPollIntervalMs = 0
      NuGetMaxAttempts = 5
      Push = false
      Check = false }

/// The common shape: one probe answers both questions, so a test about ordering
/// alone does not have to script the index and restorability separately.
let private releaseInput (repo: Repo) (rejectedTags: string list) checkFeed waitForNuGet mode =
    releaseInputWith repo rejectedTags checkFeed (fun _ id ver -> checkFeed id ver) waitForNuGet mode

let private indexOf (timeline: ResizeArray<string>) (entry: string) =
    let index = timeline.IndexOf entry

    if index < 0 then
        failwithf "expected %s on the timeline:\n%s" entry (String.concat "\n" timeline)

    index

let private pushes (timeline: ResizeArray<string>) =
    timeline |> Seq.filter (fun e -> e.StartsWith "push ") |> List.ofSeq

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a dependent's tag is not pushed until its delayed dependency is restorable`` (waitForNuGet: bool) =
    // Listed dependent-first, as FsHotWatch lists its CLI before TestPrune. The
    // dependency takes three checks to become restorable. `--skip-nuget-wait` must not
    // lift the gate: it only skips confirming the last wave.
    withRepo [ "Cli", [ "TestPrune" ]; "TestPrune", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline (Map [ "TestPrune", 1; "Cli", 1 ])
        let checkRestorable = restorable repo.Timeline (Map [ "TestPrune", 3 ])

        let output, result =
            withCapturedConsole (fun () ->
                release (releaseInputWith repo [] checkFeed checkRestorable waitForNuGet PushTags))

        let t = repo.Timeline
        test <@ result = 0 @>
        test <@ pushes t = [ "push testprune-v0.1.0-alpha.1"; "push cli-v0.1.0-alpha.1" ] @>

        let dependencyRestorable = indexOf t "restorable TestPrune 0.1.0-alpha.1 OnFeed"
        let dependentPushed = indexOf t "push cli-v0.1.0-alpha.1"

        test <@ indexOf t "restorable TestPrune 0.1.0-alpha.1 NotOnFeed" < dependencyRestorable @>
        test <@ dependencyRestorable < dependentPushed @>
        test <@ output.Contains "Publication order" @>

        // The gate asks one question of one probe: the index is not consulted about
        // the dependency at all, so there is one wait per wave, not two.
        test <@ not (t |> Seq.exists (fun e -> e.StartsWith "feed TestPrune")) @>
        test <@ not (t |> Seq.exists (fun e -> e.StartsWith "restorable(tool)")) @>

        // The final wave is confirmed only when asked to be, and by the index: the
        // consumer's own barrier is the restorability authority for the last wave.
        test <@ t.Contains "feed Cli 0.1.0-alpha.1 OnFeed" = waitForNuGet @>
        test <@ not (t.Contains "restorable Cli 0.1.0-alpha.1 OnFeed") @>)

[<Fact>]
let ``a dependency the index already lists is still held until it is restorable`` () =
    // The index lists a version minutes before a restore of it succeeds. The next
    // wave's nuspec names this exact version, so "listed" is not enough to push it.
    withRepo [ "Cli", [ "TestPrune" ]; "TestPrune", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline (Map [ "TestPrune", 1; "Cli", 1 ])
        let checkRestorable = restorable repo.Timeline (Map [ "TestPrune", 4 ])

        let output, result =
            withCapturedConsole (fun () -> release (releaseInputWith repo [] checkFeed checkRestorable false PushTags))

        let t = repo.Timeline
        test <@ result = 0 @>

        let notYet =
            t
            |> Seq.filter (fun e -> e = "restorable TestPrune 0.1.0-alpha.1 NotOnFeed")
            |> Seq.length

        test <@ notYet = 3 @>
        test <@ indexOf t "restorable TestPrune 0.1.0-alpha.1 OnFeed" < indexOf t "push cli-v0.1.0-alpha.1" @>
        test <@ output.Contains "restorable from NuGet" @>)

[<Fact>]
let ``a dependency that is a tool is gated as a tool`` () =
    // A PackAsTool package cannot be proven by a PackageReference restore, so the gate
    // must say which kind of package it is asking about rather than let every tool
    // dependency wait out the full budget and hold its dependents back.
    withRepoOf [ "Tool" ] [ "Cli", [ "Tool" ]; "Tool", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline (Map [ "Tool", 1; "Cli", 1 ])
        let checkRestorable = restorable repo.Timeline (Map [ "Tool", 1 ])

        let _, result =
            withCapturedConsole (fun () -> release (releaseInputWith repo [] checkFeed checkRestorable false PushTags))

        let t = repo.Timeline
        test <@ result = 0 @>
        test <@ indexOf t "restorable(tool) Tool 0.1.0-alpha.1" < indexOf t "push cli-v0.1.0-alpha.1" @>)

[<Fact>]
let ``a dependency that never reaches the feed holds its dependent's tag back and exits 2`` () =
    withRepo [ "Cli", [ "TestPrune" ]; "TestPrune", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline Map.empty

        let output, result =
            withCapturedConsole (fun () -> release (releaseInput repo [] checkFeed true PushTags))

        test <@ result = 2 @>
        test <@ pushes repo.Timeline = [ "push testprune-v0.1.0-alpha.1" ] @>
        test <@ not (repo.Timeline |> Seq.exists (fun e -> e.StartsWith "feed Cli")) @>
        test <@ output.Contains "held back: cli-v0.1.0-alpha.1" @>
        test <@ output.Contains "unconfirmed: TestPrune 0.1.0-alpha.1" @>)

[<Fact>]
let ``a dependency whose tag push fails is not followed by its dependent's tag`` () =
    withRepo [ "Cli", [ "TestPrune" ]; "TestPrune", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline (Map [ "TestPrune", 1; "Cli", 1 ])

        let output, result =
            withCapturedConsole (fun () ->
                release (releaseInput repo [ "testprune-v0.1.0-alpha.1" ] checkFeed true PushTags))

        test <@ result = 1 @>
        test <@ List.isEmpty (pushes repo.Timeline) @>
        test <@ output.Contains "Not pushing the tags of the packages that depend on them: cli-v0.1.0-alpha.1" @>)

[<Fact>]
let ``unrelated packages are pushed together in config order before any feed check (positive control)`` () =
    withRepo [ "Zeta", []; "Alpha", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline (Map [ "Zeta", 1; "Alpha", 1 ])

        let output, result =
            withCapturedConsole (fun () -> release (releaseInput repo [] checkFeed true PushTags))

        let t = repo.Timeline
        test <@ result = 0 @>
        test <@ pushes t = [ "push zeta-v0.1.0-alpha.1"; "push alpha-v0.1.0-alpha.1" ] @>
        test <@ indexOf t "push alpha-v0.1.0-alpha.1" < indexOf t "feed Zeta 0.1.0-alpha.1 OnFeed" @>
        test <@ not (output.Contains "Publication order") @>)

[<Fact>]
let ``a dependency cycle refuses the release before anything is written`` () =
    withRepo [ "One", [ "Two" ]; "Two", [ "One" ] ] (fun repo ->
        let checkFeed = feed repo.Timeline Map.empty

        let output, result =
            withCapturedConsole (fun () -> release (releaseInput repo [] checkFeed true PushTags))

        test <@ result = 1 @>
        test <@ repo.Timeline.Count = 0 @>
        test <@ output.Contains "cannot order the release" @>
        test <@ output.Contains "Aborting before any writes" @>

        test
            <@ readFsprojVersion (Path.Combine(repo.Root, "src", "One", "One.fsproj")) = Some(Version.parse "0.0.0") @>)

[<Fact>]
let ``a dry run prints the publication waves without pushing`` () =
    withRepo [ "Cli", [ "TestPrune" ]; "TestPrune", [] ] (fun repo ->
        let checkFeed = feed repo.Timeline Map.empty

        let output, result =
            withCapturedConsole (fun () -> release (releaseInput repo [] checkFeed true DryRun))

        test <@ result = 0 @>
        test <@ repo.Timeline.Count = 0 @>
        test <@ output.Contains "  1. TestPrune\n  2. Cli" @>)

/// A release that stops after pushing its bump commit and is resumed.
///
/// Observed 2026-11-05 in the FsHotWatch release: the consumer canary passed on
/// the pre-bump tree, the bump commit was pushed, and the wait for its CI ended on
/// one unreadable `gh` answer ("could not determine CI status") while CI was still
/// running. The resume then re-ran the canary on the post-bump tree, whose CLI
/// bundles TestPrune.Core 15.0.0 from this same release's first wave, which was not
/// published yet: the canary's restore failed with NU1102 and the release could
/// never finish.
module FsSemanticTagger.Tests.ResumedReleaseTests

open System
open System.IO
open Xunit
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.ConsumerCanary
open FsSemanticTagger.Tests.ExtractionFakes

let private copyTree (source: string) (target: string) =
    for file in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
        let destination = Path.Combine(target, Path.GetRelativePath(source, file))
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        File.Copy(file, destination)

let private versionIn (path: string) : string =
    Text.RegularExpressions.Regex.Match(File.ReadAllText path, "<Version>([^<]*)</Version>").Groups[1].Value

/// Records every canary process. A `dotnet pack` is recorded with the `<Version>`
/// its project held while it ran; a `git worktree add` populates the workspace.
let private recordingOps (commands: ResizeArray<string>) (gate: string -> GateOutcome) : Ops =
    {
        RunIn =
            fun cwd cmd args ->
                commands.Add(sprintf "%s %s" cmd args)

                if cmd = "dotnet" && args.StartsWith "pack " then
                    let project = args.Split(' ')[1]
                    commands.Add(sprintf "packed %s at %s" project (versionIn (Path.Combine(cwd, project))))

                if cmd = "git" && args.StartsWith "worktree add --detach " then
                    copyTree cwd (args.Split(' ')[3])

                Success ""
        RunGate =
            fun _ command _ logPath ->
                commands.Add(sprintf "sh -c %s" command)
                File.AppendAllText(logPath, command + "\n")
                gate command
    }

/// A machine whose config names one consumer of `package`, pinned in an fsproj.
let private canaryOn (dir: string) (package: string) (gate: string -> GateOutcome) (commands: ResizeArray<string>) =
    let repo = Path.Combine(dir, "consumer")
    Directory.CreateDirectory(Path.Combine(repo, ".git")) |> ignore
    Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore

    File.WriteAllText(
        Path.Combine(repo, "src", "C.fsproj"),
        sprintf
            """<Project><ItemGroup><PackageReference Include="%s" Version="0.0.1" /></ItemGroup></Project>"""
            package
    )

    let config =
        {
            LocalFeed = Path.Combine(dir, "feed")
            Consumers =
                [
                    {
                        Package = package
                        Repo = repo
                        Pin = "src/C.fsproj"
                        Gate = "mise run ci"
                        Timeout = TimeSpan.FromMinutes 1.0
                        Revision = Some "main"
                        Workspace = None
                        Teardown = None
                    }
                ]
        }

    let configPath = Path.Combine(dir, ".fssemantictagger.json")
    File.WriteAllText(configPath, toJson config)

    let settings =
        {
            ConfigPath = configPath
            Skip = false
            LogDir = Path.Combine(dir, "logs")
            ScratchDir = Path.Combine(dir, "scratch")
            PackagesCache = Path.Combine(dir, "cache")
            VerdictDir = Path.Combine(dir, "verdicts")
            Ops = recordingOps commands gate
        }

    settings, config

let private green =
    Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

/// One `alpha` release run of the single-package repo at `dir/producer`, at release
/// commit `head`. `extra` answers jj calls the scenario adds; `afterBump` is what
/// `gh` says once the bump commit exists. Returns the exit code, the output and
/// every command the release ran.
let private releaseAt
    (root: string)
    (head: string)
    (extra: string -> string option)
    (afterBump: CommandResult)
    (canary: Settings)
    : int * string * (string * string) list =
    let calls = ResizeArray<string * string>()
    let mutable committed = false

    let run (cmd: string) (args: string) : CommandResult =
        calls.Add((cmd, args))

        match cmd, args with
        | "jj", a when (extra a).IsSome -> Success((extra a).Value)
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "wc000"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success head
        | "jj", a when a.Contains "remote_bookmarks()" -> Success head
        | "gh", a when a.Contains "run list" -> if committed then afterBump else green
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "jj", a when a.Contains "tag list" -> Success ""
        | "git", a when a.StartsWith "tag -l" -> Success ""
        | "git", "ls-remote --tags origin" -> Success ""
        | "jj", a when a.StartsWith "tag set" -> Success ""
        | "jj", a when a.StartsWith "commit" ->
            committed <- true
            Success ""
        | "jj", a when a.StartsWith "bookmark set" -> Success ""
        | "jj", "git push" -> Success ""
        | "jj", "git export" -> Success ""
        | "git", a when a.StartsWith "push origin" -> Success ""
        | "jj", a when a = "git fetch" || a.Contains "main@origin" -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config: ToolConfig =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = Path.Combine(root, "MyLib.fsproj")
                        DllPath = "bin/Release/net10.0/MyLib.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = defaultPublishWorkflows
            CiTimeout = None
            RootDir = root
        }

    let output, code =
        withCapturedConsole (fun () ->
            Release.release
                {
                    Run = run
                    Config = config
                    Command = Release.StartAlpha
                    Mode = Release.PushTags
                    TargetPackages = []
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 50
                    TagPush =
                        {
                            PushAttempts = 1
                            PushRetryDelayMs = 0
                            RunPollIntervalMs = 0
                            RunPollAttempts = 1
                        }
                    CheckFeedPresence = fun _ _ -> Api.NotOnFeed
                    CheckRestorable = fun _ _ _ -> Api.OnFeed
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = canary
                })

    code, output, List.ofSeq calls

let private unreadable = Failure("gh: HTTP 502", 1)

let private producer (dir: string) : string =
    let root = Path.Combine(dir, "producer")
    Directory.CreateDirectory root |> ignore

    File.WriteAllText(
        Path.Combine(root, "MyLib.fsproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>\n</Project>\n"
    )

    File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- feat: something\n")
    root

/// The first run: the canary passes on `base111`, the bump commit is made and
/// pushed, and the wait for its CI ends on an unreadable answer.
let private firstRunStopsAfterBump (root: string) (canary: Settings) : string =
    let code, output, _ = releaseAt root "base111" (fun _ -> None) unreadable canary
    test <@ code = 1 @>
    test <@ output.Contains "Consumer canary: passed" @>
    test <@ output.Contains "could not determine CI status" @>
    test <@ versionIn (Path.Combine(root, "MyLib.fsproj")) = "0.1.0-alpha.1" @>
    output

/// jj's answers about `base111` -> `bump222` when the bump commit changed `changed`,
/// and the fsproj at `base111` read as `baseFsproj`.
let private bumpDiff (root: string) (changed: string) (baseFsproj: string) : string -> string option =
    let bumped = File.ReadAllText(Path.Combine(root, "MyLib.fsproj"))

    function
    | "diff --from base111 --to bump222 --name-only" -> Some changed
    | "file show -r \"base111\" \"MyLib.fsproj\"" -> Some baseFsproj
    | "file show -r \"bump222\" \"MyLib.fsproj\"" -> Some bumped
    | _ -> None

let private preBump =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>\n</Project>\n"

let private packs (commands: ResizeArray<string>) : int =
    commands |> Seq.filter (fun c -> c.StartsWith "dotnet pack") |> Seq.length

[<Fact>]
let ``resume - a canary that passed on the pre-bump tree is reused when only the bump changed, and the tags go out``
    ()
    =
    withTempDir (fun dir ->
        let root = producer dir
        let commands = ResizeArray()
        let canary, _ = canaryOn dir "MyLib" (fun _ -> Exited 0) commands
        firstRunStopsAfterBump root canary |> ignore
        test <@ packs commands = 1 @>
        commands.Clear()

        let code, output, calls =
            releaseAt root "bump222" (bumpDiff root "MyLib.fsproj\nCHANGELOG.md" preBump) green canary

        test <@ code = 0 @>
        test <@ output.Contains "Resuming in-progress release" @>
        test <@ output.Contains "Consumer canary: reused the verdict that passed on base111" @>
        test <@ output.Contains "the only change since is the release's own version bump" @>
        test <@ commands.Count = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains "tag set --allow-move v0.1.0-alpha.1")
            @>)

[<Fact>]
let ``resume - a source change since the canary ran re-runs it and says why`` () =
    withTempDir (fun dir ->
        let root = producer dir
        let commands = ResizeArray()
        let canary, _ = canaryOn dir "MyLib" (fun _ -> Exited 0) commands
        firstRunStopsAfterBump root canary |> ignore
        commands.Clear()

        let code, output, _ =
            releaseAt root "bump222" (bumpDiff root "MyLib.fsproj\nCHANGELOG.md\nsrc/Lib.fs" preBump) green canary

        test <@ code = 0 @>
        test <@ output.Contains "src/Lib.fs changed since the canary ran" @>
        test <@ not (output.Contains "Consumer canary: reused") @>
        test <@ packs commands = 1 @>)

[<Fact>]
let ``resume - an fsproj changed beyond its Version re-runs the canary`` () =
    withTempDir (fun dir ->
        let root = producer dir
        let commands = ResizeArray()
        let canary, _ = canaryOn dir "MyLib" (fun _ -> Exited 0) commands
        firstRunStopsAfterBump root canary |> ignore
        commands.Clear()

        let otherwise =
            preBump.Replace("</PropertyGroup>", "<IsPackable>true</IsPackable></PropertyGroup>")

        let code, output, _ =
            releaseAt root "bump222" (bumpDiff root "MyLib.fsproj" otherwise) green canary

        test <@ code = 0 @>
        test <@ output.Contains "MyLib.fsproj changed since the canary ran beyond its <Version>" @>
        test <@ packs commands = 1 @>)

[<Fact>]
let ``resume - a canary that refused is never reused`` () =
    withTempDir (fun dir ->
        let root = producer dir
        let commands = ResizeArray()

        let red, _ =
            canaryOn dir "MyLib" (fun c -> if c.StartsWith "dotnet restore" then Exited 0 else Exited 2) commands

        let code, output, _ = releaseAt root "base111" (fun _ -> None) green red
        test <@ code = 1 @>
        test <@ output.Contains "Consumer canary REFUSED the release" @>
        // As if a bump had been made anyway (by hand, or by a break-glass run).
        File.WriteAllText(Path.Combine(root, "MyLib.fsproj"), preBump.Replace("0.0.0", "0.1.0-alpha.1"))
        commands.Clear()

        let code, output, _ =
            releaseAt root "bump222" (bumpDiff root "MyLib.fsproj" preBump) green red

        test <@ code = 1 @>
        test <@ not (output.Contains "Consumer canary: reused") @>
        test <@ output.Contains "the canary recorded for this release plan on base111 refused" @>
        test <@ packs commands = 1 @>)

// ---- the CI wait on the bump commit ----

[<Fact>]
let ``waitForCi - an unreadable CI answer is retried, not taken as the verdict`` () =
    let mutable asked = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains "run list" ->
            asked <- asked + 1
            if asked <= 2 then unreadable else green
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result, output =
        withCapturedConsole (fun () -> Release.waitForCi run 0 10) |> fun (o, r) -> r, o

    test <@ result = Vcs.Passed @>
    test <@ asked = 3 @>
    test <@ output.Contains "could not read the CI status" @>

[<Fact>]
let ``waitForCi - an answer that stays unreadable gives up after unknownTolerance polls in a row`` () =
    let mutable asked = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains "run list" ->
            asked <- asked + 1
            unreadable
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = Release.waitForCi run 0 1000
    test <@ result = Vcs.Unknown @>
    test <@ asked = Release.unknownTolerance @>
    test <@ Release.unknownTolerance > 1 @>

[<Fact>]
let ``waitForCi - a readable answer resets the run of unreadable ones`` () =
    let mutable asked = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains "run list" ->
            asked <- asked + 1

            if asked % 2 = 0 then
                Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
            elif asked > 3 * Release.unknownTolerance then
                green
            else
                unreadable
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    test <@ Release.waitForCi run 0 1000 = Vcs.Passed @>

// ---- the canary packs the plan's unpublished dependencies too ----

/// A producer with a CLI that bundles TestPrune.Core, both separately released.
let private cliProducer (dir: string) : string =
    let root = Path.Combine(dir, "producer")

    for name, version in [ "TestPrune.Core", "14.0.0"; "FsHotWatch.Cli", "0.14.0-alpha.137" ] do
        let project = Path.Combine(root, "src", name)
        Directory.CreateDirectory project |> ignore

        File.WriteAllText(
            Path.Combine(project, name + ".fsproj"),
            sprintf """<Project><PropertyGroup><Version>%s</Version></PropertyGroup></Project>""" version
        )

    root

let private package (name: string) : PackageConfig =
    {
        Name = name
        Fsproj = sprintf "src/%s/%s.fsproj" name name
        DllPath = ""
        TagPrefix = name + "-v"
        FsProjsSharingSameTag = []
    }

let private cliPlan =
    [
        package "TestPrune.Core", parse "15.0.0"
        package "FsHotWatch.Cli", parse "0.14.0-alpha.138"
    ]

let private cliBundlesCore (published: string -> Version -> bool) : PlanContext =
    {
        DependenciesOf =
            function
            | "FsHotWatch.Cli" -> set [ "TestPrune.Core"; "NotInThisRelease" ]
            | _ -> Set.empty
        IsPublished = published
    }

[<Fact>]
let ``runWith - an unpublished planned dependency of the consumed package is packed, stamped and seeded too`` () =
    withTempDir (fun dir ->
        let root = cliProducer dir
        let commands = ResizeArray()
        let settings, config = canaryOn dir "FsHotWatch.Cli" (fun _ -> Exited 0) commands
        let corePath = Path.Combine(root, "src", "TestPrune.Core", "TestPrune.Core.fsproj")
        let coreSeen = ResizeArray()

        let settings =
            { settings with
                Ops =
                    { settings.Ops with
                        RunIn =
                            fun cwd cmd args ->
                                if args.StartsWith "pack src/FsHotWatch.Cli" then
                                    coreSeen.Add(versionIn corePath)

                                settings.Ops.RunIn cwd cmd args
                    }
            }

        let staleCore = Path.Combine(settings.PackagesCache, "testprune.core", "15.0.0")
        Directory.CreateDirectory staleCore |> ignore

        let result =
            runWith settings root (cliBundlesCore (fun _ _ -> false)) config cliPlan

        test <@ Result.isOk result @>
        test <@ commands.Contains "packed src/TestPrune.Core/TestPrune.Core.fsproj at 15.0.0" @>
        test <@ commands.Contains "packed src/FsHotWatch.Cli/FsHotWatch.Cli.fsproj at 0.14.0-alpha.138" @>
        // The CLI was built against the dependency at the version this release gives it.
        test <@ List.ofSeq coreSeen = [ "15.0.0" ] @>
        test <@ not (commands |> Seq.exists (fun c -> c.Contains "NotInThisRelease")) @>

        let seed =
            File.ReadAllText(Path.Combine(settings.ScratchDir, "fshotwatch.cli-0.14.0-alpha.138", "seed.csproj"))

        test <@ seed.Contains """<PackageDownload Include="FsHotWatch.Cli" Version="[0.14.0-alpha.138]" />""" @>
        test <@ seed.Contains """<PackageDownload Include="TestPrune.Core" Version="[15.0.0]" />""" @>
        // Every unpublished candidate leaves the cache, and the producer's bytes come back.
        test <@ not (Directory.Exists staleCore) @>
        test <@ versionIn corePath = "14.0.0" @>)

[<Fact>]
let ``runWith - a planned dependency already on the feed is not packed, seeded or evicted`` () =
    withTempDir (fun dir ->
        let root = cliProducer dir
        let commands = ResizeArray()
        let settings, config = canaryOn dir "FsHotWatch.Cli" (fun _ -> Exited 0) commands
        let publishedCore = Path.Combine(settings.PackagesCache, "testprune.core", "15.0.0")
        Directory.CreateDirectory publishedCore |> ignore

        let published (name: string) (version: Version) =
            name = "TestPrune.Core" && format version = "15.0.0"

        let result = runWith settings root (cliBundlesCore published) config cliPlan

        test <@ Result.isOk result @>
        test <@ not (commands |> Seq.exists (fun c -> c.StartsWith "dotnet pack src/TestPrune.Core")) @>

        let seed =
            File.ReadAllText(Path.Combine(settings.ScratchDir, "fshotwatch.cli-0.14.0-alpha.138", "seed.csproj"))

        test <@ not (seed.Contains "TestPrune.Core") @>
        test <@ Directory.Exists publishedCore @>)

[<Fact>]
let ``seedProjectFor - downloads every candidate a consumer resolves`` () =
    let content =
        seedProjectFor [ "FsHotWatch.Cli", "0.14.0-alpha.138"; "TestPrune.Core", "15.0.0" ]

    test <@ content.Contains """<PackageDownload Include="FsHotWatch.Cli" Version="[0.14.0-alpha.138]" />""" @>
    test <@ content.Contains """<PackageDownload Include="TestPrune.Core" Version="[15.0.0]" />""" @>
    test <@ seedProject "A" "1.0.0" = seedProjectFor [ "A", "1.0.0" ] @>

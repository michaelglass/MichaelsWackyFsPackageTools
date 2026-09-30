module FsSemanticTagger.Tests.ReleaseTests

open System.IO
open Xunit
open Tests.Common
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger
open FsSemanticTagger.Release
open FsSemanticTagger.Api
open FsSemanticTagger.Vcs
open FsSemanticTagger.Tests.ExtractionFakes

/// This module's own scratch directory, in place of the system temp dir. Its tests put
/// their fsprojs in one directory with the CHANGELOG.md a release reads beside them,
/// and the system temp dir is shared with every other process on the machine running
/// this suite, which rewrites that CHANGELOG.md mid-release. The tests in one module
/// run one at a time, so a directory per process is enough.
let private scratchDir = createTempDir ()

/// A new empty file in `scratchDir`, as `Path.GetTempFileName` makes in the temp dir.
let private scratchFile () =
    let path = Path.Combine(scratchDir, Path.GetRandomFileName())
    File.WriteAllText(path, "")
    path

/// No waits, and one tag-run poll: tests that care about the poll pass their own.
let private immediateTagPush: TagPushPolicy =
    {
        PushAttempts = 1
        PushRetryDelayMs = 0
        RunPollIntervalMs = 0
        RunPollAttempts = 1
    }

/// Prior-API stub: a FetchError, so an Auto run that reaches it aborts instead of bumping.
let private noPreviousApi (_pkg: string) (_version: string) : PreviousApiResult = FetchError "previous API unavailable"

let private noCurrentApi (_dll: string) : ApiSignature list = []

/// No machine-local canary config, and a host that must never be reached.
let private noCanary: ConsumerCanary.Settings =
    {
        ConfigPath = Path.Combine(scratchDir, "no-such-fssemantictagger.json")
        Skip = false
        LogDir = Path.Combine(scratchDir, "fssemantictagger-canary-logs")
        PackagesCache = Path.Combine(scratchDir, "fssemantictagger-canary-cache")
        Ops =
            {
                RunIn = fun _ cmd _ -> failwithf "unexpected canary process: %s" cmd
                RunGate = fun _ command _ _ -> failwithf "unexpected canary gate: %s" command
            }
    }

/// Re-seeds the temp-dir CHANGELOG.md before each release call (promotion mutates it).
let private seedTmpChangelog () =
    let p = Path.Combine(scratchDir, "CHANGELOG.md")
    File.WriteAllText(p, "# Changelog\n\n## Unreleased\n\n- test entry\n")

/// `runReleaseOnFeed` with the prior release and the current build read by
/// `previous` and `current`, grammars included.
let private runReleaseReading run config cmd mode previous current poll max push checkFeedPresence =
    seedTmpChangelog ()

    release
        {
            Run = run
            Config = { config with RootDir = scratchDir }
            Command = cmd
            Mode = mode
            TargetPackages = []
            ExtractPrevious = previous
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = current
            CiPollIntervalMs = poll
            CiWait = CiWaitTests.fixedCiWait poll max
            TagPush = immediateTagPush
            CheckFeedPresence = checkFeedPresence
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = push
            Check = false
            Canary = noCanary
        }

let private runReleaseOnFeed run config cmd mode prev cur poll max push checkFeedPresence =
    runReleaseReading
        run
        config
        cmd
        mode
        (previousWith prev (fun _ _ -> noPreviousGrammar))
        (currentWith cur (fun _ -> None))
        poll
        max
        push
        checkFeedPresence

/// Every prior version is on the feed — the default for tests not about publication.
let private runReleaseWithPush run config cmd mode prev cur poll max push =
    runReleaseOnFeed run config cmd mode prev cur poll max push (fun _ _ -> OnFeed)

let private runRelease run config cmd mode prev cur poll max =
    runReleaseWithPush run config cmd mode prev cur poll max false

/// Auto/PushTags where the test's feed decides whether a prior release is published.
let private runAutoOnFeed run config prev cur checkFeedPresence =
    runReleaseOnFeed run config Auto PushTags prev cur 0 10 false checkFeedPresence

/// The lines of `output` that say why `pkg` is, or is not, in the release plan.
let private reasonLines (pkg: string) (output: string) : string list =
    output.Split('\n')
    |> Array.map (fun line -> line.TrimEnd('\r'))
    |> Array.filter (fun line ->
        [ "Bumping "; "Resuming "; "Skipping " ]
        |> List.exists (fun verb -> line.StartsWith(verb + pkg + ": ")))
    |> Array.toList

/// A tag whose run never appeared, with the check command the poll would print.
let private missingRun (waited: System.TimeSpan) (everAnswered: bool) =
    TagConfirmationFailure.WorkflowTriggerMissing(
        "fssemantictagger-v0.14.0-alpha.8",
        waited,
        everAnswered,
        [
            "gh run list --branch fssemantictagger-v0.14.0-alpha.8 --workflow .github/workflows/release.yml --repo example/repo"
        ]
    )

[<Fact>]
let ``tag confirmation output does not claim a failed push reached the remote`` () =
    let output, result =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures
                [
                    TagConfirmationFailure.PushFailed(
                        "fssemantictagger-v0.14.0-alpha.8",
                        "the HTTPS remote has no credential helper"
                    )
                ])

    test <@ result = 1 @>
    test <@ output.Contains("tag push(es) failed") @>
    test <@ output.Contains("versions in the tree are ahead") @>
    test <@ output.Contains("resume by running the same release command again") @>
    test <@ output.Contains("If abandoning the release, reset the bumped versions") @>
    test <@ not (output.Contains("ARE on the remote")) @>
    test <@ not (output.Contains("MISSING TRIGGER")) @>

[<Fact>]
let ``tag confirmation output keeps a missing trigger distinct from a failed push`` () =
    let output, result =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures [ missingRun (System.TimeSpan.FromSeconds 300.0) true ])

    // 2, not 1: a run that has not appeared yet is not a failed release.
    test <@ result = 2 @>
    test <@ output.Contains("no workflow run YET") @>
    test <@ output.Contains("ARE on the remote") @>
    test <@ not (output.Contains("versions in the tree are ahead")) @>

[<Fact>]
let ``a release whose tag has no run yet must never be told to delete and re-push it`` () =
    // Re-pushing the tag after a run did register publishes the version twice.
    let output, _ =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures [ missingRun (System.TimeSpan.FromSeconds 300.0) true ])

    test <@ not (output.Contains(":refs/tags/")) @>
    test <@ not (output.Contains("Re-push")) @>
    test <@ output.Contains("Do NOT delete and re-push") @>
    // The exact poll query, runnable as printed.
    test
        <@
            output.Contains(
                "  gh run list --branch fssemantictagger-v0.14.0-alpha.8 --workflow .github/workflows/release.yml --repo example/repo"
            )
        @>

    test <@ not (output.Contains("<tag>")) @>

[<Fact>]
let ``the reported wait is the one actually performed, not the budget`` () =
    // The waited time comes from the clock, not the budget.
    let output, _ =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures [ missingRun (System.TimeSpan.FromSeconds 7.0) true ])

    test <@ output.Contains("asked for 7s") @>

[<Fact>]
let ``an unaskable gh is not reported as GitHub saying there is no run`` () =
    let output, _ =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures [ missingRun (System.TimeSpan.FromSeconds 300.0) false ])

    test <@ output.Contains("could not be asked") @>
    test <@ output.Contains("NOT evidence of a missing run") @>

[<Fact>]
let ``a workflow run that already failed stops the release, and says so as a failure`` () =
    // The run exists and finished: waiting cannot help, so exit 1.
    let output, result =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures
                [
                    TagConfirmationFailure.WorkflowRunFailed(
                        "fssemantictagger-v0.14.0-alpha.8",
                        [
                            {
                                Workflow = PublishWorkflow ".github/workflows/release.yml"
                                Name = "Release"
                                Url = "https://github.com/example/repo/actions/runs/42"
                                RunId = "42"
                                Status = Completed
                                Conclusion = FailureConclusion
                            }
                        ]
                    )
                ])

    test <@ result = 1 @>
    test <@ output.Contains("workflow run that FAILED") @>
    test <@ output.Contains("publish workflow .github/workflows/release.yml") @>
    test <@ output.Contains("https://github.com/example/repo/actions/runs/42") @>
    test <@ output.Contains("gh run rerun") @>
    test <@ not (output.Contains("no workflow run YET")) @>

[<Fact>]
let ``updateFsprojVersion - updates Version element in fsproj`` () =
    let tmpFile = scratchFile ()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            {
                Major = 2
                Minor = 3
                Patch = 4
                Stage = Stable
            }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>2.3.4</Version>") @>
        test <@ not (result.Contains("<Version>1.0.0</Version>")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``updateFsprojVersion - handles pre-release versions`` () =
    let tmpFile = scratchFile ()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            {
                Major = 0
                Minor = 2
                Patch = 0
                Stage = PreRelease(Alpha 1)
            }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``readFsprojVersion - reads version from fsproj`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.2.3-alpha.4</Version>
  </PropertyGroup>
</Project>"""
        )

        let result = readFsprojVersion tmpFile
        test <@ result = Some(parse "1.2.3-alpha.4") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``readFsprojVersion - returns None when no Version element`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""
        )

        let result = readFsprojVersion tmpFile
        test <@ result = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - returns 1 when uncommitted changes`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success "M src/Foo.fs"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - returns 1 when CI not passing`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - Auto with no previous tags returns 0 with no packages`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "git", arg when arg.StartsWith("tag -l") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = "src/MyLib/MyLib.fsproj"
                        DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - StartAlpha with FirstRelease tags and bumps version`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>
</Project>"""
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () ->
                runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10)

        test <@ result = 0 @>
        test <@ reasonLines "MyLib" output = [ "Bumping MyLib: first release; `alpha` requested" ] @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move v0.1.0-alpha.1"))
            @>

        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.1.0-alpha.1</Version>") @>

        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit")) @>

        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set main")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto first-releases an untagged package at its declared fsproj version`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>0.1.0-alpha.1</Version></PropertyGroup>
</Project>"""
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move v0.1.0-alpha.1"))
            @>
    finally
        File.Delete(tmpFile)

/// fakeRun responses for a passing CI and a clean working copy.
let private passingCiRun (extraResponses: (string * string * CommandResult) list) =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        let extra = extraResponses |> List.tryFind (fun (c, a, _) -> c = cmd && a = args)

        match extra with
        | Some(_, _, r) -> r
        | None ->
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | "dotnet", arg when arg.StartsWith("pack") -> Success "Successfully created package"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    (fakeRun, (fun () -> calls))

/// Bump-commit CI that outlasts 60 checks but finishes within the `CiWait` budget:
/// both waits must use that budget.
[<Fact>]
let ``release - the version-bump commit's CI wait uses the history-sized budget, like the release commit's`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (baseRun, getCalls) = passingCiRun []

        let oldFixedChecks = 60
        let bumpCiRunningChecks = oldFixedChecks + 10
        let mutable bumped = false
        let mutable bumpCiChecks = 0

        let ciAnswer status conclusion =
            sprintf
                """[{"status":"%s","conclusion":%s,"name":"CI","url":"https://example.com/1","databaseId":1,"attempt":1,"createdAt":"2026-09-27T10:00:00Z","workflowDatabaseId":7}]"""
                status
                conclusion

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", a when a.StartsWith("commit") ->
                bumped <- true
                baseRun cmd args
            | "jj", "log -r @ --no-graph -T commit_id" -> Success(if bumped then "wc2" else "wc1")
            | "jj", "log -r @- --no-graph -T commit_id" -> Success(if bumped then "bump1" else "release1")
            | "gh", a when a.Contains("--commit wc1") || a.Contains("--commit wc2") -> Success "[]"
            | "gh", a when a.Contains("--commit release1") -> Success(ciAnswer "completed" "\"success\"")
            | "gh", a when a.Contains("--commit bump1") ->
                bumpCiChecks <- bumpCiChecks + 1

                if bumpCiChecks <= bumpCiRunningChecks then
                    Success(ciAnswer "in_progress" "null")
                else
                    Success(ciAnswer "completed" "\"success\"")
            | _ -> baseRun cmd args

        // 120 checks: past the bump commit's 70.
        let mutable budgetRequests = 0

        let historySized () : CiWait.Budget =
            budgetRequests <- budgetRequests + 1

            {
                Timeout = System.TimeSpan.FromMilliseconds 119.0
                Basis = CiWait.FromHistory(System.TimeSpan.FromMilliseconds 60.0, 10)
            }

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = scratchDir
            }

        seedTmpChangelog ()

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun
                        Config = config
                        Command = StartAlpha
                        Mode = PushTags
                        TargetPackages = []
                        ExtractPrevious = noPrevious
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = noCurrent
                        CiPollIntervalMs = 0
                        CiWait = historySized
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        test <@ result = 0 @>
        test <@ not (output.Contains("CI still running after timeout")) @>
        test <@ budgetRequests = 2 @>
        test <@ output.Contains("Waiting for CI on the release commit to pass before releasing (expected ~") @>

        test
            <@ output.Contains("Waiting for CI on the version-bump commit to pass before pushing the tag (expected ~") @>

        test <@ bumpCiChecks = bumpCiRunningChecks + 1 @>

        test
            <@
                getCalls ()
                |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))
            @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - StartAlpha with LocalPublish calls dotnet pack`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha LocalPublish noPreviousApi noCurrentApi 0 10

        let calls = getCalls ()
        test <@ result = 0 @>

        // LocalPublish packs a release-shaped version, so it needs the release flag.
        test
            <@
                calls
                |> List.exists (fun (c, a) ->
                    c = "dotnet"
                    && a.StartsWith("pack")
                    && a.Contains(tmpFile)
                    && a.Contains("-p:ReleaseBuild=true"))
            @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto with reserved version bumps past it`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        // Unchanged API => patch 1.0.1, which is reserved => 1.0.2
        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let sameApi = [ ApiSignature.TypeDecl "Foo" ]
        let extractPreviousApi (_pkg: string) (_version: string) = Found sameApi

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.ofList [ "1.0.1" ]
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> sameApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.2</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto with own-changed PackAsTool package skips the API-diff (NU1212 guard) and bumps`` () =
    let tmpFile = scratchFile ()

    try
        // A PackAsTool package with a prior tag and a change in its own dir.
        File.WriteAllText(
            tmpFile,
            "<Project><PropertyGroup><PackAsTool>true</PackAsTool><Version>0.14.0-alpha.1</Version></PropertyGroup></Project>"
        )

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"cli-v*\"", Success "cli-v0.14.0-alpha.1")
                    ("jj",
                     "diff --from cli-v0.14.0-alpha.1 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        // A tool's API cannot be read (NU1212); the PackAsTool path must not ask.
        let extractPreviousApi (_pkg: string) (_version: string) : PreviousApiResult =
            FetchError "NU1212: DotnetToolReference project style can only contain references of the DotnetTool type"

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyTool"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "cli-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.14.0-alpha.2</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - non-Auto with reserved version skips package`` () =
    let (fakeRun, _getCalls) = passingCiRun []

    let config =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = "src/MyLib/MyLib.fsproj"
                        DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.ofList [ "0.1.0-alpha.1" ]
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result =
        runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - PromoteToBeta with FirstRelease returns 0 no packages`` () =
    let (fakeRun, _getCalls) = passingCiRun []

    let config =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = "src/MyLib/MyLib.fsproj"
                        DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result =
        runRelease fakeRun config PromoteToBeta PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - runs preBuildCmds before build`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, getCalls) =
            passingCiRun
                [
                    ("dotnet", "tool restore", Success "Restored.")
                    ("dotnet", "tool run paket restore", Success "Paket restored.")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = [ "dotnet tool restore"; "dotnet tool run paket restore" ]
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        let calls = getCalls ()
        test <@ result = 0 @>

        let toolRestoreIdx =
            calls |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "tool restore")

        let paketRestoreIdx =
            calls
            |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "tool run paket restore")

        let buildIdx =
            calls |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "build -c Release")

        test <@ toolRestoreIdx < paketRestoreIdx @>
        test <@ paketRestoreIdx < buildIdx @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForCi - polls until CI passes`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            if ghCallCount <= 2 then
                Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
            else
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 10 // 0ms poll interval for tests
    test <@ result = Passed @>
    test <@ ghCallCount = 3 @>

[<Fact>]
let ``waitForCi - exact workflow query ignores same-SHA skipped release and docs jobs`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            Success
                """[{"databaseId":101,"attempt":1,"createdAt":"2026-08-31T12:00:00Z","workflowDatabaseId":77,"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/ci"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 3
    test <@ result = Passed @>
    test <@ ghCallCount = 1 @>

[<Fact>]
let ``waitForCi - times out when CI stays in progress`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 3 // max 3 attempts

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

[<Fact>]
let ``waitForCi - returns Failed immediately without polling`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            Success """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 10

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ ghCallCount = 1 @>

[<Fact>]
let ``release - skips packages with no changes since last tag`` () =
    let tmpFileA = scratchFile ()

    try
        File.WriteAllText(tmpFileA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("liba-v") -> Success "liba-v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from liba-v0.1.0-alpha.1") -> Success "1 file changed"
            // LibB has a tag but no changes.
            | "jj", a when a.Contains("tag list") && a.Contains("libb-v") -> Success "libb-v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from libb-v0.1.0-alpha.1") -> Success ""

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = tmpFileA
                            DllPath = "src/LibA/bin/Release/net10.0/LibA.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibB"
                            Fsproj = "src/LibB/LibB.fsproj"
                            DllPath = "src/LibB/bin/Release/net10.0/LibB.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move liba-v"))
            @>

        test
            <@
                not (
                    calls
                    |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move libb-v"))
                )
            @>
    finally
        File.Delete(tmpFileA)

[<Fact>]
let ``release - Auto detects breaking API change and bumps major`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let oldApi =
            [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "Bar(): String") ]

        let currentApi = [ ApiSignature.TypeDecl "Foo" ]

        let extractPreviousApi (_pkg: string) (_version: string) = Found oldApi

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Breaking on v1+ => 2.0.0
        test <@ content.Contains("<Version>2.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto refuses to guess when the current build's API cannot be read`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let unreadable (_dll: string) : Extraction.ExtractedDll =
            {
                Api = Error "could not load fake.dll: Could not find assembly 'Gone'"
                Grammar = GrammarUnreadable "could not load fake.dll: Could not find assembly 'Gone'"
            }

        let output, result =
            withCapturedConsole (fun () ->
                runReleaseReading
                    fakeRun
                    config
                    Auto
                    PushTags
                    (previousWith (fun _ _ -> Found [ ApiSignature.TypeDecl "Foo" ]) (fun _ _ -> noPreviousGrammar))
                    unreadable
                    0
                    10
                    false
                    (fun _ _ -> OnFeed))

        test <@ result = 1 @>

        test
            <@
                output.Contains
                    "MyLib: could not read the public API of the current build (could not load fake.dll: Could not find assembly 'Gone')"
            @>

        test <@ (File.ReadAllText tmpFile).Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

/// A CLI with the one command `check-api`.
let private checkApiGrammar =
    {
        Roots = [ Leaf("check-api", [], []) ]
        GlobalFlags = []
    }

/// `checkApiGrammar` with the command renamed `diff-api` (a breaking change) and
/// given a `--wait` flag whose env prefix is unknown (a caveat).
let private diffApiGrammar =
    {
        Roots =
            [
                Leaf(
                    "diff-api",
                    [],
                    [
                        GrammarBuilders.flag "wait" Nullary
                        |> GrammarBuilders.withEnv (Some(EnvVarUnknownPrefix "WAIT"))
                    ]
                )
            ]
        GlobalFlags = []
    }

/// Auto-release a library `MyLib` at 1.0.0 whose API is unchanged and whose
/// current build has the CLI grammar `diffApiGrammar`, reading the previous
/// release's grammar as `previousGrammar`. Returns the output, the exit code and
/// the fsproj afterwards.
let private releaseLibraryAgainstGrammar (previousGrammar: GrammarRead) =
    let dir =
        Path.Combine(scratchDir, "fsst-grammar-fold-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore

    try
        let fsproj = Path.Combine(dir, "MyLib.fsproj")
        File.WriteAllText(fsproj, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")
        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + dir + "/**\"", Success "1 file changed")
                ]

        let api = [ ApiSignature.TypeDecl "Foo" ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = fsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun
                        Config = config
                        Command = Auto
                        Mode = PushTags
                        TargetPackages = []
                        ExtractPrevious = previousWith (fun _ _ -> Found api) (fun _ _ -> previousGrammar)
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = currentWith (fun _ -> api) (fun _ -> Some diffApiGrammar)
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        output, result, File.ReadAllText fsproj
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact>]
let ``release - Auto folds a breaking grammar change into the bump when the API is unchanged`` () =
    // Identical API, but the CLI grammar renamed a command: must bump major.
    let output, result, fsproj =
        releaseLibraryAgainstGrammar (GrammarModelled checkApiGrammar)

    test <@ result = 0 @>
    test <@ output.Contains "note: MyLib: the CLI's env-var prefix is not a string literal" @>
    test <@ fsproj.Contains "<Version>2.0.0</Version>" @>

[<Fact>]
let ``release - Auto notes a previous CLI grammar it cannot model and lets the API decide`` () =
    let output, result, fsproj =
        releaseLibraryAgainstGrammar (GrammarNotModellable "it has no root command union")

    test <@ result = 0 @>

    test
        <@
            output.Contains
                "note: MyLib: the CLI grammar of the previous release v1.0.0 could not be modelled (it has no root command union), so the API diff alone decides the bump"
        @>

    test <@ fsproj.Contains "<Version>1.0.1</Version>" @>

/// Auto/PushTags with an identical API, so any bump above patch comes from the
/// changelog. Returns the captured output and the exit code.
let private releaseWithUnchangedApiAs
    (mode: ReleaseMode)
    (check: bool)
    (run: string -> string -> CommandResult)
    (config: ToolConfig)
    (only: string list)
    =
    let api = [ ApiSignature.TypeDecl "Foo" ]

    withCapturedConsole (fun () ->
        release
            {
                Run = run
                Config = config
                Command = Auto
                Mode = mode
                TargetPackages = only
                ExtractPrevious = previousWith (fun _ _ -> Found api) (fun _ _ -> noPreviousGrammar)
                ExtractCachedPrevious = noCachedPrevious
                ExtractCurrent = currentWith (fun _ -> api) (fun _ -> None)
                CiPollIntervalMs = 0
                CiWait = CiWaitTests.fixedCiWait 0 10
                TagPush = immediateTagPush
                CheckFeedPresence = (fun _ _ -> OnFeed)
                CheckRestorable = (fun _ _ _ -> OnFeed)
                WaitForNuGet = false
                NuGetPollIntervalMs = 0
                NuGetMaxAttempts = 1
                Push = false
                Check = check
                Canary = noCanary
            })

let private releaseWithUnchangedApi = releaseWithUnchangedApiAs PushTags false

/// A single-package repo in `dir` at `version`, with `changelog` as its root CHANGELOG.md.
let private singlePackageRepo (dir: string) (version: string) (changelog: string) =
    let fsproj = Path.Combine(dir, "MyLib.fsproj")

    File.WriteAllText(fsproj, sprintf "<Project><PropertyGroup><Version>%s</Version></PropertyGroup></Project>" version)

    File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), changelog)

    let (fakeRun, _getCalls) =
        passingCiRun
            [
                ("git", "tag -l \"v*\"", Success("v" + version))
                ("jj", sprintf "diff --from v%s --to @ --summary \"glob:%s/**\"" version dir, Success "1 file changed")
            ]

    let config =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = fsproj
                        DllPath = "fake.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = dir
        }

    fsproj, fakeRun, config

[<Fact>]
let ``release - Auto floors the bump at major when the changelog declares a breaking change the API diff cannot see``
    ()
    =
    // A changed `[<Literal>]` is inlined into consumers and invisible to the API diff:
    // the author's `feat!:` must make it 8.0.0.
    withTempDir (fun dir ->
        let fsproj, run, config =
            singlePackageRepo dir "7.0.0" "# Changelog\n\n## Unreleased\n\n- feat!: SchemaVersion 9 -> 10\n"

        let output, result = releaseWithUnchangedApi run config []

        test <@ result = 0 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>8.0.0</Version>") @>
        test <@ output.Contains "MyLib: " @>
        test <@ output.Contains "declares a breaking change" @>
        test <@ output.Contains "- feat!: SchemaVersion 9 -> 10" @>)

/// The entry TestPrune shipped: it reads as breaking but is no marker.
let private unrecognisedBreakingChangelog =
    "# Changelog\n\n## Unreleased\n\n- **BREAKING (API): Audit.ownIds → Audit.observe**\n"

[<Fact>]
let ``release --dry-run - warns about an entry that looks breaking but is no marker`` () =
    withTempDir (fun dir ->
        let fsproj, run, config =
            singlePackageRepo dir "7.0.0" unrecognisedBreakingChangelog

        let output, result = releaseWithUnchangedApiAs DryRun false run config []

        test <@ result = 0 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>7.0.0</Version>") @>
        test <@ output.Contains "7.0.1" @>
        test <@ output.Split("warning:").Length = 2 @>

        test
            <@
                output.Contains(
                    sprintf
                        "MyLib: warning: %s: the entry `- **BREAKING (API): Audit.ownIds → Audit.observe**` starts with BREAKING but is not a breaking-change marker"
                        (Path.Combine(dir, "CHANGELOG.md"))
                )
            @>)

[<Fact>]
let ``release --check - warns about an entry that looks breaking but is no marker`` () =
    withTempDir (fun dir ->
        let _, run, config = singlePackageRepo dir "7.0.0" unrecognisedBreakingChangelog

        let output, result = releaseWithUnchangedApiAs DryRun true run config []

        test <@ result = 0 @>
        test <@ output.Contains "Release will promote:" @>
        test <@ output.Contains "MyLib: warning: " @>
        test <@ output.Contains "`feat!:` (any `<type>!:`) or `BREAKING CHANGE:`" @>)

[<Fact>]
let ``release --check - a recognised breaking marker draws no warning`` () =
    withTempDir (fun dir ->
        let _, run, config =
            singlePackageRepo dir "7.0.0" "# Changelog\n\n## Unreleased\n\n- BREAKING CHANGE: a\n- feat!: b\n"

        let output, result = releaseWithUnchangedApiAs DryRun true run config []

        test <@ result = 0 @>
        test <@ not (output.Contains "warning:") @>)

[<Fact>]
let ``release - Auto keeps a declared fix with an unchanged API at a patch, and reports no disagreement`` () =
    withTempDir (fun dir ->
        let fsproj, run, config =
            singlePackageRepo dir "7.0.0" "# Changelog\n\n## Unreleased\n\n- fix: handle a null\n"

        let output, result = releaseWithUnchangedApi run config []

        test <@ result = 0 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>7.0.1</Version>") @>
        test <@ not (output.Contains "declares") @>)

[<Fact>]
let ``release - Auto honours a breaking marker in a section derived from commit summaries`` () =
    // A `feat!:` commit promoted into an empty Unreleased must bump major too.
    withTempDir (fun dir ->
        let fsproj, baseRun, config =
            singlePackageRepo dir "1.2.3" "# Changelog\n\n## Unreleased\n\n## 1.2.3 - 2026-01-01\n\n- old\n"

        let run cmd (args: string) =
            if cmd = "jj" && args.StartsWith("log -r \"v1.2.3..@\"") then
                Success "feat!: drop the v1 wire format\u001e"
            else
                baseRun cmd args

        let output, result = releaseWithUnchangedApi run config []

        test <@ result = 0 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>2.0.0</Version>") @>
        test <@ output.Contains "feat!: drop the v1 wire format" @>)

[<Fact>]
let ``release - Auto takes the strongest declaration across every changelog behind one tag`` () =
    // A declaration in the shared project's changelog counts too.
    withTempDir (fun dir ->
        let coreDir = Path.Combine(dir, "core")
        let cliDir = Path.Combine(dir, "cli")
        Directory.CreateDirectory coreDir |> ignore
        Directory.CreateDirectory cliDir |> ignore
        let coreFsproj = Path.Combine(coreDir, "Core.fsproj")
        let cliFsproj = Path.Combine(cliDir, "Cli.fsproj")

        for fsproj in [ coreFsproj; cliFsproj ] do
            File.WriteAllText(fsproj, "<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>")

        File.WriteAllText(Path.Combine(coreDir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- fix: a\n")
        File.WriteAllText(Path.Combine(cliDir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- feat!: b\n")

        let (run, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"core-v*\"", Success "core-v2.0.0")
                    ("jj",
                     sprintf "diff --from core-v2.0.0 --to @ --summary \"glob:%s/**\"" coreDir,
                     Success "1 file changed")
                ]

        let package name fsproj prefix shared =
            {
                Name = name
                Fsproj = fsproj
                DllPath = "fake.dll"
                TagPrefix = prefix
                FsProjsSharingSameTag = shared
            }

        let config =
            {
                Packages =
                    [
                        package "Core" coreFsproj "core-v" [ cliFsproj ]
                        package "Other" (Path.Combine(dir, "other", "Other.fsproj")) "other-v" []
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let output, result = releaseWithUnchangedApi run config [ "Core" ]

        test <@ result = 0 @>
        test <@ (File.ReadAllText coreFsproj).Contains("<Version>3.0.0</Version>") @>
        test <@ output.Contains(Path.Combine(cliDir, "CHANGELOG.md")) @>)

[<Fact>]
let ``release - Auto detects addition and bumps minor`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let oldApi = [ ApiSignature.TypeDecl "Foo" ]

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let extractPreviousApi (_pkg: string) (_version: string) = Found oldApi

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Addition on v1+ => 1.1.0
        test <@ content.Contains("<Version>1.1.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto aborts (no bump) when previous API cannot be read`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let extractPreviousApi (_pkg: string) (_version: string) = FetchError "feed unreachable"

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        // Refuse to guess: non-zero exit, fsproj untouched.
        test <@ result = 1 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto skips an orphan tag and diffs against the last published prior`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>")

        // v1.2.0 never reached NuGet, so diff against v1.1.0.
        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.1.0\nv1.2.0")
                    ("jj",
                     "diff --from v1.2.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let oldApi = [ ApiSignature.TypeDecl "Foo" ]

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let extractPreviousApi (_pkg: string) (version: string) =
            match version with
            | "1.2.0" -> NotRestorable "error NU1102: Unable to find package MyLib with version (= 1.2.0)"
            | "1.1.0" -> Found oldApi // the last published prior
            | other -> failwithf "unexpected version fetch: %s" other

        let checkFeedPresence (_pkg: string) (version: string) =
            match version with
            | "1.2.0" -> NotOnFeed
            | _ -> OnFeed

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () ->
                runAutoOnFeed fakeRun config extractPreviousApi (fun _ -> currentApi) checkFeedPresence)

        test <@ result = 0 @>
        test <@ output.Contains("v1.2.0") && output.Contains("orphan") @>

        test
            <@
                reasonLines "MyLib" output =
                    [
                        "Bumping MyLib: own change since v1.2.0 — public API diffed against v1.1.0, the newest published release: an addition (Foo::NewMethod(): String)"
                    ]
            @>
        // Bump off 1.2.0 with the v1.1.0 diff (addition) => 1.3.0.
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.3.0</Version>") @>
    finally
        File.Delete(tmpFile)

/// The previous release is on the feed but its assembly will not load: it must
/// not be treated as an orphan and diffed against an older baseline.
let private unreadableBaselineRun (tmpFile: string) (latest: string) =
    passingCiRun
        [
            ("git", "tag -l \"v*\"", Success("v1.0.0\nv1.1.0\nv" + latest))
            ("jj",
             "diff --from v"
             + latest
             + " --to @ --summary \"glob:"
             + Path.GetDirectoryName(tmpFile)
             + "/**\"",
             Success "1 file changed")
        ]

let private unreadableBaselineConfig (tmpFile: string) =
    {
        Packages =
            [
                {
                    Name = "MichaelGlass.FSharp.Analyzers"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = []
                }
            ]
        ReservedVersions = Set.empty
        PreBuildCmds = []
        PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
        CiTimeout = None
        RootDir = ""
    }

let private analyzerLoadFailure =
    "could not load /home/u/.nuget/packages/michaelglass.fsharp.analyzers/1.2.0/analyzers/dotnet/fs/MichaelGlass.FSharp.Analyzers.dll: Could not find assembly 'FSharp.Analyzers.SDK, Version=0.39.0.0, Culture=neutral, PublicKeyToken=null'."

/// Auto with an unreadable `latest` and a readable v1.1.0, recording which versions were fetched.
let private releaseOverUnreadableBaseline (latest: string) (checkFeedPresence: string -> string -> FeedPresence) =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            sprintf "<Project><PropertyGroup><Version>%s</Version></PropertyGroup></Project>" latest
        )

        let fetched = System.Collections.Generic.List<string>()

        let extractPreviousApi (_pkg: string) (version: string) =
            fetched.Add version

            if version = latest then
                Unreadable analyzerLoadFailure
            else
                Found [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "Removed(): String") ]

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let (fakeRun, _getCalls) = unreadableBaselineRun tmpFile latest

        let output, result =
            withCapturedConsole (fun () ->
                runAutoOnFeed
                    fakeRun
                    (unreadableBaselineConfig tmpFile)
                    extractPreviousApi
                    (fun _ -> currentApi)
                    checkFeedPresence)

        output, result, List.ofSeq fetched, File.ReadAllText(tmpFile)
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto fails closed, never walking back, when the published previous release's API cannot be read`` () =
    let output, result, fetched, fsproj =
        releaseOverUnreadableBaseline "1.2.0" (fun _ _ -> OnFeed)

    test <@ not (output.Contains("orphan")) @>
    test <@ not (output.Contains("not on the feed")) @>
    // No walk back to v1.1.0.
    test <@ fetched = [ "1.2.0" ] @>
    test <@ result = 1 @>
    test <@ fsproj.Contains("<Version>1.2.0</Version>") @>
    test <@ output.Contains("cannot determine the version bump") @>
    test <@ output.Contains("previous release v1.2.0, which is published") @>
    test <@ output.Contains("MichaelGlass.FSharp.Analyzers.dll") @>
    test <@ output.Contains("Could not find assembly 'FSharp.Analyzers.SDK") @>

[<Fact>]
let ``release - Auto treats an unreachable feed as published when the previous API cannot be read`` () =
    // A feed that does not answer fails closed, like OnFeed.
    let output, result, fetched, _ =
        releaseOverUnreadableBaseline "1.2.0" (fun _ _ -> FeedUnknown "The operation has timed out.")

    test <@ not (output.Contains("orphan")) @>
    test <@ fetched = [ "1.2.0" ] @>
    test <@ result = 1 @>

[<Fact>]
let ``release - Auto proceeds without walking back when an unreadable baseline cannot change a pre-release bump`` () =
    // An alpha bumps to alpha.N+1 whatever the diff says, so it proceeds, but still
    // without an orphan warning or an older baseline.
    let output, result, fetched, fsproj =
        releaseOverUnreadableBaseline "1.2.0-alpha.4" (fun _ _ -> OnFeed)

    test <@ not (output.Contains("orphan")) @>
    test <@ fetched = [ "1.2.0-alpha.4" ] @>
    test <@ result = 0 @>
    test <@ output.Contains("the public API of v1.2.0-alpha.4 could not be read") @>
    test <@ output.Contains("does not depend on the API diff") @>
    test <@ fsproj.Contains("<Version>1.2.0-alpha.5</Version>") @>

[<Fact>]
let ``release - Auto still skips a genuinely unpublished release whose API cannot be read (positive control)`` () =
    // A real orphan: warned about and walked past to v1.1.0.
    let checkFeedPresence (_pkg: string) (version: string) =
        match version with
        | "1.2.0" -> NotOnFeed
        | _ -> OnFeed

    let output, result, fetched, fsproj =
        releaseOverUnreadableBaseline "1.2.0" checkFeedPresence

    test <@ output.Contains("tag v1.2.0 is not on the feed (orphan tag") @>
    test <@ fetched = [ "1.2.0"; "1.1.0" ] @>
    test <@ result = 0 @>
    // v1.1.0 had Foo::Removed: breaking on 1.x => major.
    test <@ fsproj.Contains("<Version>2.0.0</Version>") @>

[<Fact>]
let ``release - Auto still aborts on a transient fetch error (does not skip)`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.1.0\nv1.2.0")
                    ("jj",
                     "diff --from v1.2.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        // A transient error on the newest tag aborts; walking back could under-bump.
        let extractPreviousApi (_pkg: string) (version: string) =
            match version with
            | "1.2.0" -> FetchError "connection timed out"
            | other -> failwithf "must not walk past a transient error; got fetch for %s" other

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 1 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.2.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto when every prior tag is absent on feed bumps conservatively`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.1.0\nv1.2.0")
                    ("jj",
                     "diff --from v1.2.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let extractPreviousApi (_pkg: string) (_version: string) =
            NotRestorable "error NU1102: Unable to find package MyLib"

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () ->
                runAutoOnFeed fakeRun config extractPreviousApi (fun _ -> currentApi) (fun _ _ -> NotOnFeed))

        // Nothing published to diff against => NoChange => 1.2.1.
        test <@ result = 0 @>

        test
            <@
                reasonLines "MyLib" output =
                    [
                        "Bumping MyLib: own change since v1.2.0; no prior release reached the feed, so there is no published API to diff against"
                    ]
            @>

        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.2.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto every prior tag absent honours the reserved-version skip`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.2.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.1.0\nv1.2.0")
                    ("jj",
                     "diff --from v1.2.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let extractPreviousApi (_pkg: string) (_version: string) =
            NotRestorable "error NU1102: Unable to find package MyLib"

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                // 1.2.1 is reserved => 1.2.2.
                ReservedVersions = Set.ofList [ "1.2.1" ]
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runAutoOnFeed fakeRun config extractPreviousApi (fun _ -> currentApi) (fun _ _ -> NotOnFeed)

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.2.2</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto pre-1.0 breaking change bumps minor (UnionConfig 0.3.0 -> 0.4.0)`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.3.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v0.3.0")
                    ("jj",
                     "diff --from v0.3.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let oldApi =
            [
                ApiSignature.TypeDecl "ConfigVarKind"
                ApiSignature.TypeDecl "ConfigVarKind+AutoGenerated"
                ApiSignature.Member("AutoGenerated", "initialValue: FSharpOption<String>")
            ]

        let currentApi =
            [
                ApiSignature.TypeDecl "ConfigVarKind"
                ApiSignature.TypeDecl "ConfigVarKind+AutoGenerated"
            ]

        let extractPreviousApi (_pkg: string) (_version: string) = Found oldApi

        let config =
            {
                Packages =
                    [
                        {
                            Name = "UnionConfig"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Pre-1.0 breaking => 0.4.0, not 0.3.1
        test <@ content.Contains("<Version>0.4.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI fails`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success
                        """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>

        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git export")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI times out`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 3

        test <@ result = 1 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI has no runs`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success "[]"
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>

        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

/// `gh run list` JSON with one successful run.
let private greenCiJson =
    """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

/// fakeRun cases for a pushed release commit with green CI; callers add their own.
let private pushedGreenCi (cmd: string) (args: string) : CommandResult option =
    match cmd, args with
    | "jj", "diff --summary" -> Some(Success "")
    | "jj", "log -r @ --no-graph -T commit_id" -> Some(Success "abc123")
    | "jj", "log -r @- --no-graph -T commit_id" -> Some(Success "parent1")
    | "jj", a when a.Contains("remote_bookmarks()") -> Some(Success "parent1") // pushed
    | "gh", a when a.Contains("run list") -> Some(Success greenCiJson)
    | _ -> None

[<Fact>]
let ``release - reconciles coverage via loosen-from-ci after CI is green`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        match pushedGreenCi cmd args with
        | Some r -> r
        | None ->
            match cmd, args with
            | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
            | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") -> Success ""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

    // CI is confirmed green before loosen-from-ci.
    test <@ calls |> List.exists (fun (c, a) -> c = "gh" && a.Contains("run list")) @>

    test
        <@
            calls
            |> List.exists (fun (c, a) -> c = "dotnet" && a.Contains("coverageratchet loosen-from-ci"))
        @>

[<Fact>]
let ``release - returns 1 when coverageratchet loosen-from-ci fails`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match pushedGreenCi cmd args with
        | Some r -> r
        | None ->
            match cmd, args with
            | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
            | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") -> Failure("CI not green", 1)
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - prints coverageratchet error message when loosen-from-ci fails`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match pushedGreenCi cmd args with
        | Some r -> r
        | None ->
            match cmd, args with
            | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
            | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") ->
                Failure("coverage threshold mismatch", 1)
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

    test <@ result = 1 @>
    test <@ output.Contains("coverage threshold mismatch") @>

[<Fact>]
let ``release - fails fast with actionable push-first message when commit isn't pushed`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

    test <@ result = 1 @>

    test <@ output.Contains("hasn't been pushed") @>
    test <@ output.Contains("--push") @>
    test <@ output.Contains("loosen-from-ci") @>

    test <@ not (output.Contains("CI failed")) @>

    test <@ not (calls |> List.exists (fun (c, a) -> c = "gh" && a.Contains("run list"))) @>
    test <@ not (calls |> List.exists (fun (c, a) -> c = "dotnet" && a = "build -c Release")) @>

    test
        <@
            not (
                calls
                |> List.exists (fun (c, a) -> c = "dotnet" && a.Contains("loosen-from-ci"))
            )
        @>

    test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>

[<Fact>]
let ``release - with --push pushes the commit then waits for CI when not pushed`` () =
    // Not pushed until --push pushes it; then CI is green.
    let mutable pushed = false
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success(if pushed then "parent1" else "")
        | "jj", "git push" ->
            pushed <- true
            Success ""
        | "gh", a when a.Contains("run list") -> Success greenCiJson
        | "dotnet", "tool list" -> Success "" // no coverageratchet -> reconciliation no-op
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "git", arg when arg.StartsWith("tag -l") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result =
        runReleaseWithPush fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10 true

    test <@ result = 0 @>
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push") @>

[<Fact>]
let ``release - distinguishes a genuine CI failure from an unpushed commit`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1" // pushed
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/9"}]"""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

    test <@ result = 1 @>
    test <@ output.Contains("CI failed") @>
    test <@ output.Contains("https://example.com/9") @>
    test <@ not (output.Contains("hasn't been pushed")) @>

[<Fact>]
let ``release - returns 1 when CI status is Unknown`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") -> Failure("gh not installed", 1)
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - waits then returns 1 when pushed CI times out still in progress`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 2

    test <@ result = 1 @>

[<Fact>]
let ``release - returns 1 when the release commit sha can't be determined`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @- --no-graph -T commit_id" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success ""
        | "git", "rev-parse HEAD" -> Failure("not a git repo", 1)
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

    test <@ result = 1 @>
    test <@ output.Contains("could not determine the release commit") @>

[<Fact>]
let ``release - pushed commit whose CI run never registers times out`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1" // pushed
        | "gh", a when a.Contains("run list") -> Success "[]"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages = []
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 2)

    test <@ result = 1 @>
    test <@ output.Contains("no CI run registered") @>
    test <@ not (output.Contains("CI failed")) @>
    test <@ output.Contains("giving up after") @>
    test <@ not (output.Contains("~1-2 min")) @>

[<Fact>]
let ``release - PromoteToRC with HasPreviousRelease succeeds`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0-beta.3</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0-beta.3")
                    ("jj",
                     "diff --from v1.0.0-beta.3 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config PromoteToRC PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0-rc.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - PromoteToStable with HasPreviousRelease succeeds`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0-rc.1</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0-rc.1")
                    ("jj",
                     "diff --from v1.0.0-rc.1 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config PromoteToStable PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - PromoteToBeta with HasPreviousRelease succeeds`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.1.0-alpha.3</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v0.1.0-alpha.3")
                    ("jj",
                     "diff --from v0.1.0-alpha.3 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config PromoteToBeta PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.1.0-beta.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForCi - returns Passed immediately when CI passes`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 10
    test <@ result = Passed @>
    test <@ ghCallCount = 1 @>

[<Fact>]
let ``waitForCi - returns NoRuns immediately`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") -> Success "[]"
        | "jj", "diff --summary" -> Success "M src/Foo.fs"
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 10
    test <@ result = NoRuns @>

[<Fact>]
let ``waitForCi - returns Unknown immediately`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
        | "gh", a when a.Contains("run list") -> Failure("gh not found", 1)
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

    let result = waitForCi run 0 10
    test <@ result = Unknown @>

[<Fact>]
let ``release - updates fsProjsSharingSameTag versions too`` () =
    let tmpFileMain = scratchFile ()
    let tmpFileShared = scratchFile ()

    try
        File.WriteAllText(tmpFileMain, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        File.WriteAllText(tmpFileShared, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) = passingCiRun []

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFileMain
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = [ tmpFileShared ]
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let mainContent = File.ReadAllText(tmpFileMain)
        let sharedContent = File.ReadAllText(tmpFileShared)
        test <@ mainContent.Contains("<Version>0.1.0-alpha.1</Version>") @>
        test <@ sharedContent.Contains("<Version>0.1.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFileMain)
        File.Delete(tmpFileShared)

[<Fact>]
let ``release - resumes when fsproj already has target version (idempotent)`` () =
    let tmpFile = scratchFile ()

    try
        // A previous run already bumped to 0.2.0-alpha.1.
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>

        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set"))) @>

        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin")) @>

        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - fails fast when resuming and CI has failed`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable ghCallCount = 0

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success
                        """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - a tag that triggered no workflow run fails the release`` () =
    // Every push succeeds but GitHub creates no run for the tag, so nothing publishes.
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") && a.Contains("--branch") -> Success "[]"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result <> 0 @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - resumes and polls when CI is in progress`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                elif ghCallCount <= 3 then
                    Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
                else
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // 1 pre-release + 2 in-progress + 1 success, plus 1 tag-run check per tag.
        test <@ ghCallCount = 5 @>
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push") @>
        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - second run after successful first run produces no changes`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.2.0-alpha.1"
            | "jj", a when a.Contains("--from v0.2.0-alpha.1") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - aborts with exit 1 when CHANGELOG has no Unreleased section`` () =
    let tmpFile = scratchFile ()
    let changelogPath = Path.Combine(scratchDir, "CHANGELOG.md")

    try
        let fsprojBefore =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojBefore)
        File.WriteAllText(changelogPath, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- stuff\n")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = scratchDir
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = StartAlpha
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 1 @>
        test <@ File.ReadAllText(tmpFile) = fsprojBefore @>
    finally
        File.Delete(tmpFile)

        if File.Exists changelogPath then
            File.Delete changelogPath

[<Fact>]
let ``release - dryRun skips uncommitted check and does not write fsproj`` () =
    let tmpFile = scratchFile ()

    try
        let fsprojBefore =
            """<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>"""

        File.WriteAllText(tmpFile, fsprojBefore)

        let mutable calls = []

        // Uncommitted changes would abort a real release; dry-run skips that check.
        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha DryRun noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        test <@ File.ReadAllText(tmpFile) = fsprojBefore @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>
        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("run list"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "dotnet" && a = "build -c Release")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - dryRun with missing Unreleased warns but still returns 0`` () =
    let tmpFile = scratchFile ()
    let tmpDir = createTempDir ()

    try
        let fsprojPath = Path.Combine(tmpDir, "MyLib.fsproj")
        File.WriteAllText(fsprojPath, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = fsprojPath
                            DllPath = "x.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = tmpDir
            }

        // rootDir has no CHANGELOG.md.
        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun
                        Config = config
                        Command = StartAlpha
                        Mode = DryRun
                        TargetPackages = []
                        ExtractPrevious = noPrevious
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = noCurrent
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        test <@ result = 0 @>

        test
            <@
                output.ToLowerInvariant().Contains("warning")
                || output.ToLowerInvariant().Contains("changelog")
            @>

        test <@ File.ReadAllText(fsprojPath).Contains("<Version>0.0.0</Version>") @>
    finally
        File.Delete(tmpFile)
        cleanupDir tmpDir

[<Fact>]
let ``release - resume in DryRun mode takes no actions and returns 0`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha DryRun noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>

        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - resume with LocalPublish packs without pushing`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "dotnet", arg when arg.StartsWith("pack") -> Success "Successfully created package"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha LocalPublish noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "dotnet" && a.StartsWith("pack") && a.Contains(tmpFile))
            @>

        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForNuGet - returns NO unconfirmed packages when all are already published`` () =
    let mutable checks = 0

    let checkFeedPresence (_id: string) (_ver: string) =
        checks <- checks + 1
        OnFeed

    let result = waitForNuGet checkFeedPresence 0 5 [ "PkgA", "1.0.0"; "PkgB", "2.0.0" ]

    test <@ List.isEmpty result @>
    test <@ checks = 2 @>

[<Fact>]
let ``waitForNuGet - polls until a package becomes available`` () =
    let mutable attempts = 0

    let checkFeedPresence (_id: string) (_ver: string) =
        attempts <- attempts + 1
        if attempts >= 3 then OnFeed else NotOnFeed

    let result = waitForNuGet checkFeedPresence 0 10 [ "PkgA", "1.0.0" ]

    test <@ List.isEmpty result @>
    test <@ attempts >= 3 @>

[<Fact>]
let ``waitForNuGet - names the package it could not confirm (times out)`` () =
    let checkFeedPresence (_id: string) (_ver: string) = NotOnFeed
    let result = waitForNuGet checkFeedPresence 0 3 [ "PkgA", "1.0.0" ]
    test <@ result = [ "PkgA", "1.0.0" ] @>

[<Fact>]
let ``waitForNuGet - maxAttempts 1 does exactly one check then times out`` () =
    let mutable checks = 0

    let checkFeedPresence (_id: string) (_ver: string) =
        checks <- checks + 1
        NotOnFeed

    let result = waitForNuGet checkFeedPresence 0 1 [ "PkgA", "1.0.0" ]
    test <@ result = [ "PkgA", "1.0.0" ] @>
    test <@ checks = 1 @>

/// Like runRelease, but the caller drives the NuGet-availability wait.
let private runReleaseWithNuGetWait run config cmd checkFeedPresence maxAttempts =
    seedTmpChangelog ()

    release
        {
            Run = run
            Config = { config with RootDir = scratchDir }
            Command = cmd
            Mode = PushTags
            TargetPackages = []
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 10
            TagPush = immediateTagPush
            CheckFeedPresence = checkFeedPresence
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = true
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = maxAttempts
            Push = false
            Check = false
            Canary = noCanary
        }

[<Fact>]
let ``release - waits for NuGet after pushing tags and checks the published package`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _getCalls) = passingCiRun []

        let mutable checked' = []

        let checkFeedPresence (id: string) (ver: string) =
            checked' <- checked' @ [ (id, ver) ]
            OnFeed

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result = runReleaseWithNuGetWait fakeRun config StartAlpha checkFeedPresence 5

        test <@ result = 0 @>
        test <@ checked' |> List.contains ("MyLib", "0.1.0-alpha.1") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - an unconfirmed NuGet wait exits 2, not 0`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _getCalls) = passingCiRun []

        let checkFeedPresence (_id: string) (_ver: string) = NotOnFeed

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result = runReleaseWithNuGetWait fakeRun config StartAlpha checkFeedPresence 2

        // 2, not 0: nobody has seen the packages. Not 1 either: the tags are pushed.
        test <@ result = 2 @>
    finally
        File.Delete(tmpFile)

/// Positive control: a release whose packages appear exits 0.
[<Fact>]
let ``release - a fully confirmed NuGet wait still exits 0`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _getCalls) = passingCiRun []

        let checkFeedPresence (_id: string) (_ver: string) = OnFeed

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result = runReleaseWithNuGetWait fakeRun config StartAlpha checkFeedPresence 2

        test <@ result = 0 @>
    finally
        File.Delete(tmpFile)


let private pkg name fsproj prefix : PackageConfig =
    {
        Name = name
        Fsproj = fsproj
        DllPath = sprintf "%s.dll" name
        TagPrefix = prefix
        FsProjsSharingSameTag = []
    }

[<Fact>]
let ``selectPackages - empty target returns all packages unchanged`` () =
    let pkgs = [ pkg "A" "a.fsproj" "a-v"; pkg "B" "b.fsproj" "b-v" ]
    test <@ selectPackages [] pkgs = Ok pkgs @>

[<Fact>]
let ``selectPackages - single name returns only that package`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"
    test <@ selectPackages [ "B" ] [ a; b ] = Ok [ b ] @>

[<Fact>]
let ``selectPackages - multiple names return those packages preserving order`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"
    let c = pkg "C" "c.fsproj" "c-v"
    test <@ selectPackages [ "A"; "C" ] [ a; b; c ] = Ok [ a; c ] @>

[<Fact>]
let ``selectPackages - unknown name errors listing valid names`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"

    match selectPackages [ "Nope" ] [ a; b ] with
    | Error msg ->
        test <@ msg.Contains("Nope") @>
        test <@ msg.Contains("A") && msg.Contains("B") @>
    | Ok _ -> failwith "expected an error for an unknown package name"

[<Fact>]
let ``selectPackages - one unknown among known names still errors`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"

    match selectPackages [ "A"; "Nope" ] [ a; b ] with
    | Error msg -> test <@ msg.Contains("Nope") @>
    | Ok _ -> failwith "expected an error when any name is unknown"

/// Like runRelease, with an `--only` package list.
let private runReleaseTargeting run config cmd mode targets =
    seedTmpChangelog ()

    release
        {
            Run = run
            Config = { config with RootDir = scratchDir }
            Command = cmd
            Mode = mode
            TargetPackages = targets
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 10
            TagPush = immediateTagPush
            CheckFeedPresence = (fun _ _ -> OnFeed)
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = false
            Check = false
            Canary = noCanary
        }

[<Fact>]
let ``release - scoped to one package only tags that package`` () =
    let tmpFileA = scratchFile ()

    try
        File.WriteAllText(tmpFileA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = tmpFileA
                            DllPath = "src/LibA/bin/Release/net10.0/LibA.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        // LibB's fsproj does not exist: processing it would crash.
                        {
                            Name = "LibB"
                            Fsproj = "/no/such/LibB.fsproj"
                            DllPath = "src/LibB/bin/Release/net10.0/LibB.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result = runReleaseTargeting fakeRun config StartAlpha PushTags [ "LibA" ]

        let calls = getCalls ()
        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move liba-v"))
            @>

        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("libb-v"))) @>
    finally
        File.Delete(tmpFileA)

[<Fact>]
let ``release - --only on a multi-package repo uses the per-package CHANGELOG, not repo root`` () =
    // `--only` must not turn a multi-package repo into a single-package one: the root
    // has no CHANGELOG.md, so only the per-package lookup can succeed.
    let pkgDir = createTempDir ()
    let rootDir = createTempDir ()

    try
        let fsprojA = Path.Combine(pkgDir, "LibA.fsproj")
        File.WriteAllText(fsprojA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        File.WriteAllText(
            Path.Combine(pkgDir, "CHANGELOG.md"),
            "# Changelog\n\n## Unreleased\n\n- feat: a real change\n"
        )

        let (fakeRun, getCalls) = passingCiRun []

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = fsprojA
                            DllPath = "a.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibB"
                            Fsproj = "/no/such/LibB.fsproj"
                            DllPath = "b.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = rootDir
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = StartAlpha
                    Mode = PushTags
                    TargetPackages = [ "LibA" ]
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        let calls = getCalls ()
        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move liba-v"))
            @>

        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("libb-v"))) @>
    finally
        cleanupDir pkgDir
        cleanupDir rootDir

[<Fact>]
let ``release - scoped to multiple packages tags exactly those`` () =
    let tmpA = scratchFile ()
    let tmpC = scratchFile ()

    try
        File.WriteAllText(tmpA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        File.WriteAllText(tmpC, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = tmpA
                            DllPath = "a.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibB"
                            Fsproj = "/no/such/LibB.fsproj"
                            DllPath = "b.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibC"
                            Fsproj = tmpC
                            DllPath = "c.dll"
                            TagPrefix = "libc-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runReleaseTargeting fakeRun config StartAlpha PushTags [ "LibA"; "LibC" ]

        let calls = getCalls ()
        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move liba-v"))
            @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move libc-v"))
            @>

        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("libb-v"))) @>
    finally
        File.Delete(tmpA)
        File.Delete(tmpC)

[<Fact>]
let ``release - unknown target package aborts with exit 1 before any work`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]
        Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config =
        {
            Packages =
                [
                    {
                        Name = "LibA"
                        Fsproj = "a.fsproj"
                        DllPath = "a.dll"
                        TagPrefix = "liba-v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = ""
        }

    let output, result =
        withCapturedConsole (fun () -> runReleaseTargeting fakeRun config StartAlpha PushTags [ "Nope" ])

    test <@ result = 1 @>
    test <@ output.Contains("Nope") && output.Contains("LibA") @>
    test <@ List.isEmpty calls @>

[<Fact>]
let ``release - scoping composes with dry-run (only target previewed)`` () =
    let tmpA = scratchFile ()

    try
        File.WriteAllText(tmpA, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.Contains("tag list") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = tmpA
                            DllPath = "a.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibB"
                            Fsproj = "/no/such/LibB.fsproj"
                            DllPath = "b.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () -> runReleaseTargeting fakeRun config StartAlpha DryRun [ "LibA" ])

        test <@ result = 0 @>
        test <@ output.Contains("Targeting: LibA") @>
        test <@ not (output.Contains("LibB")) @>
        test <@ File.ReadAllText(tmpA).Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpA)

// Resume of a bumped-but-untagged release: fsproj version ahead of the latest
// tag, and no tag at that version.

[<Fact>]
let ``release - Auto resumes when fsproj is ahead of last tag and no tag at that version (even if previous API unreadable)``
    ()
    =
    let tmpFile = scratchFile ()

    try
        // Last tag alpha.16, fsproj alpha.17, no alpha.17 tag.
        File.WriteAllText(
            tmpFile,
            "<Project><PropertyGroup><Version>0.8.0-alpha.17</Version></PropertyGroup></Project>"
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:core-v") -> Success "core-v0.8.0-alpha.16"
            | "jj", "tag list core-v0.8.0-alpha.17" -> Success ""
            | "git", "tag -l core-v0.8.0-alpha.17" -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "FsHotWatch"
                            Fsproj = tmpFile
                            DllPath = "src/FsHotWatch/bin/Release/net10.0/FsHotWatch.dll"
                            TagPrefix = "core-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        // The previous API is unreadable: resume must short-circuit before the diff.
        let output, result =
            withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

        test <@ result = 0 @>

        test
            <@
                reasonLines "FsHotWatch" output =
                    [
                        sprintf
                            "Resuming FsHotWatch: %s declares 0.8.0-alpha.17, which has no tag yet (a release bumped it and stopped before tagging). Finishing that release."
                            tmpFile
                    ]
            @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move core-v0.8.0-alpha.17"))
            @>

        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set"))) @>
        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.8.0-alpha.17</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto dry-run reports the resume plan instead of 'No packages to release'`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            "<Project><PropertyGroup><Version>0.8.0-alpha.17</Version></PropertyGroup></Project>"
        )

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:core-v") -> Success "core-v0.8.0-alpha.16"
            | "jj", "tag list core-v0.8.0-alpha.17" -> Success ""
            | "git", "tag -l core-v0.8.0-alpha.17" -> Success ""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "FsHotWatch"
                            Fsproj = tmpFile
                            DllPath = "x.dll"
                            TagPrefix = "core-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () -> runRelease fakeRun config Auto DryRun noPreviousApi noCurrentApi 0 10)

        test <@ result = 0 @>
        test <@ not (output.Contains("No packages to release")) @>
        test <@ output.Contains("FsHotWatch: resuming in-progress release -> tag core-v0.8.0-alpha.17") @>
        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.8.0-alpha.17</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto with fsproj equal to last tag has nothing to do (not a resume)`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(
            tmpFile,
            "<Project><PropertyGroup><Version>0.8.0-alpha.16</Version></PropertyGroup></Project>"
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:core-v") -> Success "core-v0.8.0-alpha.16"
            | "jj", a when a.Contains("--from core-v0.8.0-alpha.16") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "FsHotWatch"
                            Fsproj = tmpFile
                            DllPath = "x.dll"
                            TagPrefix = "core-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

        test <@ result = 0 @>
        test <@ output.Contains("No packages to release") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

/// A local tag at HEAD that was never pushed or published: no changes can appear
/// "since" it. With the package `NotOnFeed` the release resumes that same version,
/// leaving the tag in place and the version and changelog as they are.
let private orphanTagFakeRun (tmpFile: string) =
    passingCiRun
        [
            ("git", "tag -l \"v*\"", Success "v0.3.2\nv0.3.3\nv0.3.4")
            ("jj",
             "diff --from v0.3.4 --to @ --summary \"glob:"
             + Path.GetDirectoryName(tmpFile)
             + "/**\"",
             Success "")
            ("jj", "tag list v0.3.4", Success "v0.3.4")
        ]

/// The single-package config for the tests above.
let private orphanTagConfig (tmpFile: string) =
    {
        Packages =
            [
                {
                    Name = "Falco.UnionRoutes"
                    Fsproj = tmpFile
                    DllPath = "x.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = []
                }
            ]
        ReservedVersions = Set.empty
        PreBuildCmds = []
        PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
        CiTimeout = None
        RootDir = ""
    }

/// Like `runRelease`, but drives the feed seam. The previous API stays unreadable,
/// so the orphan decision can only come from the feed.
let private runReleaseWithFeed run config checkFeedPresence =
    seedTmpChangelog ()

    release
        {
            Run = run
            Config = { config with RootDir = scratchDir }
            Command = Auto
            Mode = PushTags
            TargetPackages = []
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 10
            TagPush = immediateTagPush
            CheckFeedPresence = checkFeedPresence
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = false
            Check = false
            Canary = noCanary
        }

/// A library and a `PackAsTool` CLI (whose API cannot be probed): the orphan
/// tests run against both.
let private libraryFsproj =
    "<Project><PropertyGroup><Version>0.3.4</Version></PropertyGroup></Project>"

let private toolFsproj =
    "<Project><PropertyGroup><Version>0.3.4</Version><PackAsTool>true</PackAsTool></PropertyGroup></Project>"

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``release - orphan newest tag with no changes resumes that same version in place`` (packAsTool: bool) =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, (if packAsTool then toolFsproj else libraryFsproj))

        let checkFeedPresence (_pkg: string) (version: string) =
            match version with
            | "0.3.4" -> NotOnFeed
            | other -> failwithf "should only ask about the wedged version; got %s" other

        let (fakeRun, getCalls) = orphanTagFakeRun tmpFile

        let output, result =
            withCapturedConsole (fun () -> runReleaseWithFeed fakeRun (orphanTagConfig tmpFile) checkFeedPresence)

        let calls = getCalls ()

        test <@ result = 0 @>
        test <@ not (output.Contains("No packages to release")) @>
        test <@ not (output.Contains("no changes since v0.3.4")) @>
        test <@ output.Contains("Falco.UnionRoutes: resuming in-progress release -> tag v0.3.4") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a = "push origin v0.3.4") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set"))) @>
        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.3.4</Version>") @>
    finally
        File.Delete(tmpFile)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``release - published newest tag with no changes still skips (no spurious release)`` (packAsTool: bool) =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, (if packAsTool then toolFsproj else libraryFsproj))

        // The feed has 0.3.4: the release is finished, so this stays a no-op.
        let checkFeedPresence (_pkg: string) (_version: string) = OnFeed

        let (fakeRun, getCalls) = orphanTagFakeRun tmpFile

        let output, result =
            withCapturedConsole (fun () -> runReleaseWithFeed fakeRun (orphanTagConfig tmpFile) checkFeedPresence)

        let calls = getCalls ()

        test <@ result = 0 @>
        test <@ output.Contains("Skipping Falco.UnionRoutes: no changes since v0.3.4") @>
        test <@ output.Contains("No packages to release") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
    finally
        File.Delete(tmpFile)

/// Any feed failure is `FeedUnknown`, and must never trigger a republish.
[<Theory>]
[<InlineData(false, "The operation has timed out.")>]
[<InlineData(true, "The operation has timed out.")>]
[<InlineData(false, "HTTP 503 for https://api.nuget.org/v3-flatcontainer/falco.unionroutes/index.json")>]
[<InlineData(true, "HTTP 503 for https://api.nuget.org/v3-flatcontainer/falco.unionroutes/index.json")>]
[<InlineData(false, "HTTP 401 for https://api.nuget.org/v3-flatcontainer/falco.unionroutes/index.json")>]
[<InlineData(true, "unreadable flat-container index for Falco.UnionRoutes")>]
[<InlineData(true, "No such host is known.")>]
let ``release - unreachable feed on the newest tag never triggers a republish`` (packAsTool: bool) (reason: string) =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, (if packAsTool then toolFsproj else libraryFsproj))

        let checkFeedPresence (_pkg: string) (_version: string) = FeedUnknown reason

        let (fakeRun, getCalls) = orphanTagFakeRun tmpFile

        let output, result =
            withCapturedConsole (fun () -> runReleaseWithFeed fakeRun (orphanTagConfig tmpFile) checkFeedPresence)

        let calls = getCalls ()

        test <@ result = 0 @>
        test <@ output.Contains("Skipping Falco.UnionRoutes: no changes since v0.3.4") @>
        test <@ not (output.Contains("orphan tag")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
    finally
        File.Delete(tmpFile)

/// A published package can ship no DLL (e.g. RefStamp), so its API reads as
/// `Unreadable`. The feed decides, and says published: skip, without asking the extractor.
[<Fact>]
let ``release - a published package whose DLL is unreadable is never republished`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, libraryFsproj)

        let mutable apiConsulted = false

        let extractPreviousApi (_pkg: string) (_version: string) =
            apiConsulted <- true
            Unreadable "Falco.UnionRoutes 0.3.4 is in the NuGet cache but ships no Falco.UnionRoutes.dll"

        let (fakeRun, getCalls) = orphanTagFakeRun tmpFile

        let output, result =
            withCapturedConsole (fun () ->
                seedTmpChangelog ()

                release
                    {
                        Run = fakeRun
                        Config =
                            { orphanTagConfig tmpFile with
                                RootDir = scratchDir
                            }
                        Command = Auto
                        Mode = PushTags
                        TargetPackages = []
                        ExtractPrevious = previousWith extractPreviousApi (fun _ _ -> noPreviousGrammar)
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = noCurrent
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        let calls = getCalls ()

        test <@ result = 0 @>
        test <@ output.Contains("Skipping Falco.UnionRoutes: no changes since v0.3.4") @>
        test <@ output.Contains("No packages to release") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not apiConsulted @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - orphan newest tag is not resumed when the tree declares a different version`` () =
    let tmpFile = scratchFile ()

    try
        // The tree still says 0.3.3: resuming would publish 0.3.3 under the 0.3.4 tag.
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.3.3</Version></PropertyGroup></Project>")

        let checkFeedPresence (_pkg: string) (_version: string) = NotOnFeed

        let (fakeRun, getCalls) = orphanTagFakeRun tmpFile

        let output, result =
            withCapturedConsole (fun () -> runReleaseWithFeed fakeRun (orphanTagConfig tmpFile) checkFeedPresence)

        let calls = getCalls ()

        test <@ result = 0 @>
        test <@ output.Contains("Skipping Falco.UnionRoutes: no changes since v0.3.4") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.3.3</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - fresh changes still bump normally (not treated as resume)`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj",
                     "diff --from v1.0.0 --to @ --summary \"glob:"
                     + Path.GetDirectoryName(tmpFile)
                     + "/**\"",
                     Success "1 file changed")
                ]

        let oldApi = [ ApiSignature.TypeDecl "Foo" ]

        let currentApi =
            [
                ApiSignature.TypeDecl "Foo"
                ApiSignature.Member("Foo", "NewMethod(): String")
            ]

        let extractPreviousApi (_pkg: string) (_version: string) = Found oldApi

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        // Addition on v1+ => 1.1.0
        test <@ File.ReadAllText(tmpFile).Contains("<Version>1.1.0</Version>") @>
    finally
        File.Delete(tmpFile)

/// Release `MyLib` 1.0.0 (tag v1.0.0, one own change since) with `cmd`, its prior
/// release read by `previous` and its current build by `current`. Returns the
/// output and the exit code.
let private releaseLibraryReading (cmd: ReleaseCommand) previous current =
    withTempDir (fun dir ->
        let _fsproj, fakeRun, config =
            singlePackageRepo dir "1.0.0" "# Changelog\n\n## Unreleased\n\n- fix: a change\n"

        withCapturedConsole (fun () ->
            runReleaseReading fakeRun config cmd PushTags previous current 0 10 false (fun _ _ -> OnFeed)))

/// `releaseLibraryReading` with the prior and current API both `[ type Foo ]` and
/// no CLI grammar.
let private releaseUnchangedApi (cmd: ReleaseCommand) =
    let api = [ ApiSignature.TypeDecl "Foo" ]

    releaseLibraryReading
        cmd
        (previousWith (fun _ _ -> Found api) (fun _ _ -> noPreviousGrammar))
        (currentWith (fun _ -> api) (fun _ -> None))

[<Fact>]
let ``release - a library whose current grammar cannot be read is bumped by its API diff alone`` () =
    let api = [ ApiSignature.TypeDecl "Foo" ]

    let output, result =
        releaseLibraryReading
            Auto
            (previousWith (fun _ _ -> Found api) (fun _ _ -> GrammarModelled checkApiGrammar))
            (fun _ ->
                {
                    Api = Ok api
                    Grammar = GrammarUnreadable "could not load fake.dll: bad image"
                })

    test <@ result = 0 @>

    test
        <@
            reasonLines "MyLib" output =
                [
                    "Bumping MyLib: own change since v1.0.0 — public API diffed: no public API change"
                ]
        @>


[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``release - Auto first release ships the declared version unless it is reserved`` (reserved: bool) =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) = passingCiRun [ ("git", "tag -l \"v*\"", Success "") ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = if reserved then Set.ofList [ "1.0.0" ] else Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let output, result =
            withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

        test <@ result = 0 @>

        if reserved then
            test <@ output.Contains "Warning: version 1.0.0 is reserved, skipping MyLib (first release)" @>
            test <@ List.isEmpty (reasonLines "MyLib" output) @>
            test <@ output.Contains "No packages to release" @>
        else
            test <@ reasonLines "MyLib" output = [ "Bumping MyLib: first release at declared version 1.0.0" ] @>
    finally
        File.Delete(tmpFile)

// A library whose own change leaves its public API unchanged (FsHotWatch.Coverage:
// a dependency bump and a CHANGELOG entry) was planned with no line saying why.
[<Fact>]
let ``release - a library bumped with an unchanged API says why, once`` () =
    let output, result = releaseUnchangedApi Auto

    test <@ result = 0 @>

    test
        <@
            reasonLines "MyLib" output =
                [
                    "Bumping MyLib: own change since v1.0.0 — public API diffed: no public API change"
                ]
        @>

[<Fact>]
let ``release - an explicit command's own-change bump says why, once`` () =
    let output, result = releaseUnchangedApi StartAlpha

    test <@ result = 0 @>
    test <@ reasonLines "MyLib" output = [ "Bumping MyLib: own change since v1.0.0; `alpha` requested" ] @>

[<Fact>]
let ``release - multi-package mixed: one mid-release resumes, one fresh bumps`` () =
    let tmpResume = scratchFile ()
    let tmpFresh = scratchFile ()

    try
        // LibA: ahead of its tag, no tag at its version => resume.
        File.WriteAllText(
            tmpResume,
            "<Project><PropertyGroup><Version>0.2.0-alpha.2</Version></PropertyGroup></Project>"
        )
        // LibB: at its tag, with changes => fresh bump.
        File.WriteAllText(
            tmpFresh,
            "<Project><PropertyGroup><Version>0.5.0-alpha.1</Version></PropertyGroup></Project>"
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:liba-v") -> Success "liba-v0.2.0-alpha.1"
            | "jj", "tag list liba-v0.2.0-alpha.2" -> Success ""
            | "git", "tag -l liba-v0.2.0-alpha.2" -> Success ""
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:libb-v") -> Success "libb-v0.5.0-alpha.1"
            | "jj", a when a.Contains("--from libb-v0.5.0-alpha.1") -> Success "1 file changed"
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "LibA"
                            Fsproj = tmpResume
                            DllPath = "a.dll"
                            TagPrefix = "liba-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "LibB"
                            Fsproj = tmpFresh
                            DllPath = "b.dll"
                            TagPrefix = "libb-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move liba-v0.2.0-alpha.2"))
            @>
        // nextAlphaCycle(0.5.0-alpha.1) = 0.6.0-alpha.1
        test <@ File.ReadAllText(tmpFresh).Contains("<Version>0.6.0-alpha.1</Version>") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move libb-v0.6.0-alpha.1"))
            @>

        test <@ File.ReadAllText(tmpResume).Contains("<Version>0.2.0-alpha.2</Version>") @>
    finally
        File.Delete(tmpResume)
        File.Delete(tmpFresh)


/// A temp repo where the tool `src/Tool/Tool.fsproj` references `src/Dep/Dep.fsproj`.
/// Returns the tool fsproj's absolute path.
let private writeBundlingRepo (root: string) (toolVersion: string) =
    let toolDir = Path.Combine(root, "src", "Tool")
    let depDir = Path.Combine(root, "src", "Dep")
    Directory.CreateDirectory(toolDir) |> ignore
    Directory.CreateDirectory(depDir) |> ignore
    let toolFsproj = Path.Combine(toolDir, "Tool.fsproj")

    File.WriteAllText(
        toolFsproj,
        sprintf
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>%s</Version></PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Dep/Dep.fsproj\" />\n  </ItemGroup>\n</Project>"
            toolVersion
    )

    File.WriteAllText(
        Path.Combine(depDir, "Dep.fsproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>"
    )

    File.WriteAllText(
        Path.Combine(root, "CHANGELOG.md"),
        "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- old\n"
    )

    toolFsproj

let private runReleaseInRoot run config cmd =
    release
        {
            Run = run
            Config = config
            Command = cmd
            Mode = PushTags
            TargetPackages = []
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 10
            TagPush = immediateTagPush
            CheckFeedPresence = (fun _ _ -> OnFeed)
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = false
            Check = false
            Canary = noCanary
        }

[<Fact>]
let ``release - Auto rebundles when only a bundled dependency changed`` () =
    withTempDir (fun root ->
        let toolFsproj = writeBundlingRepo root "1.0.0"
        let ownSrcDir = Path.Combine(root, "src", "Tool")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "v1.0.0"
            | "jj", a when a.Contains("--from v1.0.0") && a.Contains(ownSrcDir) -> Success ""
            | "jj", a when a.Contains("--from v1.0.0") && a.Contains("src/Dep") -> Success "1 file changed"
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Tool"
                            Fsproj = toolFsproj
                            DllPath = "src/Tool/bin/Release/net10.0/Tool.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result = runReleaseInRoot fakeRun config Auto

        test <@ result = 0 @>
        // Rebundle: patch bump, and the prior API is never fetched.
        let content = File.ReadAllText toolFsproj
        test <@ content.Contains("<Version>1.0.1</Version>") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move v1.0.1"))
            @>
        // An empty Unreleased gets the default rebundle bullet.
        let changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"))
        test <@ changelog.Contains "- chore: rebuild to bundle updated dependencies" @>
        test <@ changelog.Contains "## 1.0.1 -" @>)

[<Fact>]
let ``release - Auto skips when neither own nor dependency changed`` () =
    withTempDir (fun root ->
        let toolFsproj = writeBundlingRepo root "1.0.0"
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "v1.0.0"
            | "jj", a when a.Contains("--from v1.0.0") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Tool"
                            Fsproj = toolFsproj
                            DllPath = "src/Tool/bin/Release/net10.0/Tool.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result = runReleaseInRoot fakeRun config Auto

        test <@ result = 0 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ File.ReadAllText(toolFsproj).Contains("<Version>1.0.0</Version>") @>)

[<Fact>]
let ``release - own change still uses API diff, ignoring dependency`` () =
    withTempDir (fun root ->
        let toolFsproj = writeBundlingRepo root "1.0.0"

        File.WriteAllText(
            Path.Combine(root, "CHANGELOG.md"),
            "# Changelog\n\n## Unreleased\n\n- feat: own work\n\n## 1.0.0 - 2026-01-01\n"
        )

        let ownSrcDir = Path.Combine(root, "src", "Tool")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "v1.0.0"
            | "jj", a when a.Contains("--from v1.0.0") && a.Contains(ownSrcDir) -> Success "1 file changed"
            | "jj", a when a.Contains("--from v1.0.0") -> Success "1 file changed"
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        // Minor, not a rebundle's patch: the API diff ran.
        let oldApi = [ ApiSignature.TypeDecl "Foo" ]

        let currentApi =
            [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "New(): String") ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Tool"
                            Fsproj = toolFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = previousWith (fun _ _ -> Found oldApi) (fun _ _ -> noPreviousGrammar)
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = currentWith (fun _ -> currentApi) (fun _ -> None)
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 0 @>
        test <@ File.ReadAllText(toolFsproj).Contains("<Version>1.1.0</Version>") @>)

[<Fact>]
let ``release - explicit command rebundles on dependency-only change`` () =
    withTempDir (fun root ->
        let toolFsproj = writeBundlingRepo root "0.1.0-alpha.3"
        let ownSrcDir = Path.Combine(root, "src", "Tool")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "v0.1.0-alpha.3"
            | "jj", a when a.Contains("--from v0.1.0-alpha.3") && a.Contains(ownSrcDir) -> Success ""
            | "jj", a when a.Contains("--from v0.1.0-alpha.3") && a.Contains("src/Dep") -> Success "1 file changed"
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Tool"
                            Fsproj = toolFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        // Only the bundled dependency changed, but the explicit alpha -> beta still applies.
        let result = runReleaseInRoot fakeRun config PromoteToBeta

        test <@ result = 0 @>
        test <@ File.ReadAllText(toolFsproj).Contains("<Version>0.1.0-beta.1</Version>") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move v0.1.0-beta.1"))
            @>

        let changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"))
        test <@ changelog.Contains "- chore: rebuild to bundle updated dependencies" @>)

[<Fact>]
let ``release - dependency-only rebundle skips a reserved explicit version`` () =
    withTempDir (fun root ->
        let toolFsproj = writeBundlingRepo root "0.1.0-alpha.3"
        let ownSrcDir = Path.Combine(root, "src", "Tool")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "v0.1.0-alpha.3"
            | "jj", a when a.Contains("--from v0.1.0-alpha.3") && a.Contains(ownSrcDir) -> Success ""
            | "jj", a when a.Contains("--from v0.1.0-alpha.3") && a.Contains("src/Dep") -> Success "1 file changed"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Tool"
                            Fsproj = toolFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.ofList [ "0.1.0-beta.1" ]
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result = runReleaseInRoot fakeRun config PromoteToBeta

        test <@ result = 0 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ File.ReadAllText(toolFsproj).Contains("<Version>0.1.0-alpha.3</Version>") @>)


/// A temp repo where the library `src/Lib/Lib.fsproj` references `src/Core/Core.fsproj`,
/// which is released separately, so Lib does not bundle it. Returns Lib's fsproj path.
let private writeLibraryRepo (root: string) (libVersion: string) =
    let libDir = Path.Combine(root, "src", "Lib")
    let coreDir = Path.Combine(root, "src", "Core")
    Directory.CreateDirectory(libDir) |> ignore
    Directory.CreateDirectory(coreDir) |> ignore
    let libFsproj = Path.Combine(libDir, "Lib.fsproj")

    File.WriteAllText(
        libFsproj,
        sprintf
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>%s</Version></PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Core/Core.fsproj\" />\n  </ItemGroup>\n</Project>"
            libVersion
    )

    File.WriteAllText(
        Path.Combine(coreDir, "Core.fsproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>"
    )

    File.WriteAllText(Path.Combine(libDir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n")
    File.WriteAllText(Path.Combine(coreDir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n## 2.0.0 - 2026-01-01\n")
    libFsproj

[<Fact>]
let ``release - library does NOT rebundle when only a separately-published dependency changed`` () =
    withTempDir (fun root ->
        let libFsproj = writeLibraryRepo root "1.0.0"
        let libDir = Path.Combine(root, "src", "Lib")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") && arg.Contains("lib-v") -> Success "lib-v1.0.0"
            | "git", arg when arg.StartsWith("tag -l") && arg.Contains("core-v") -> Success "core-v2.0.0"
            | "jj", a when a.Contains("--from lib-v1.0.0") && a.Contains(libDir) -> Success ""
            | "jj", a when a.Contains("--from core-v2.0.0") -> Success ""
            // Lib excludes Core; a Core-dir query answered here would show up as a rebundle.
            | "jj", a when a.Contains("--from lib-v1.0.0") -> Success "1 file changed"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Lib"
                            Fsproj = libFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "lib-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "Core"
                            Fsproj = "src/Core/Core.fsproj"
                            DllPath = "fake.dll"
                            TagPrefix = "core-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 0 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ File.ReadAllText(libFsproj).Contains("<Version>1.0.0</Version>") @>)

[<Fact>]
let ``release - PackAsTool rebundles when a separately-published bundled dependency changed`` () =
    withTempDir (fun root ->
        // A PackAsTool bundles Core even though Core is released separately.
        let cliDir = Path.Combine(root, "src", "Cli")
        let coreDir = Path.Combine(root, "src", "Core")
        Directory.CreateDirectory(cliDir) |> ignore
        Directory.CreateDirectory(coreDir) |> ignore
        let cliFsproj = Path.Combine(cliDir, "Cli.fsproj")

        File.WriteAllText(
            cliFsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>1.0.0</Version><PackAsTool>true</PackAsTool></PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Core/Core.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        File.WriteAllText(
            Path.Combine(coreDir, "Core.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(cliDir, "CHANGELOG.md"),
            "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n"
        )

        File.WriteAllText(
            Path.Combine(coreDir, "CHANGELOG.md"),
            "# Changelog\n\n## Unreleased\n\n## 2.0.0 - 2026-01-01\n"
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") && arg.Contains("cli-v") -> Success "cli-v1.0.0"
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.Contains("--from cli-v1.0.0") && a.Contains(cliDir) -> Success ""
            | "jj", a when a.Contains("--from cli-v1.0.0") && a.Contains("src/Core") -> Success "1 file changed"
            | "jj", a when a.Contains("--from cli-v1.0.0") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Cli"
                            Fsproj = cliFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "cli-v"
                            FsProjsSharingSameTag = []
                        }
                        {
                            Name = "Core"
                            Fsproj = "src/Core/Core.fsproj"
                            DllPath = "fake.dll"
                            TagPrefix = "core-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = [ "Cli" ]
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 0 @>
        test <@ File.ReadAllText(cliFsproj).Contains("<Version>1.0.1</Version>") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move cli-v1.0.1"))
            @>)

[<Fact>]
let ``release - library rebundles when a non-configured helper dependency changed`` () =
    withTempDir (fun root ->
        // Helper is not a configured package, so Lib bundles it.
        let libDir = Path.Combine(root, "src", "Lib")
        let helperDir = Path.Combine(root, "src", "Helper")
        Directory.CreateDirectory(libDir) |> ignore
        Directory.CreateDirectory(helperDir) |> ignore
        let libFsproj = Path.Combine(libDir, "Lib.fsproj")

        File.WriteAllText(
            libFsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><Version>1.0.0</Version></PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Helper/Helper.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        File.WriteAllText(
            Path.Combine(helperDir, "Helper.fsproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(libDir, "CHANGELOG.md"),
            "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n"
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success "lib-v1.0.0"
            | "jj", a when a.Contains("--from lib-v1.0.0") && a.Contains(libDir) -> Success ""
            | "jj", a when a.Contains("--from lib-v1.0.0") && a.Contains("src/Helper") -> Success "1 file changed"
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Lib"
                            Fsproj = libFsproj
                            DllPath = "fake.dll"
                            TagPrefix = "lib-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 0 @>
        test <@ File.ReadAllText(libFsproj).Contains("<Version>1.0.1</Version>") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set --allow-move lib-v1.0.1"))
            @>)

// If pushing the bump commit fails, no local tag may exist: resume keys off
// "no tag at the fsproj version".

[<Fact>]
let ``release - pushes main before creating tags so a push failure leaves no orphan local tag`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Failure("push failed: remote rejected", 1)
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let threw =
            try
                runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10
                |> ignore

                false
            with _ ->
                true

        test <@ threw @>

        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git export")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)


let private rs = string (char 0x1e)

/// The jj args descriptionsSinceTag issues for v1.0.0 over one dir.
let private descArgsFor (ownDir: string) =
    sprintf "log -r \"v1.0.0..@\" --no-graph -T \"description ++ \\\"\\x1e\\\"\" \"%s\"" ownDir

let private releaseInput run config cmd mode check : ReleaseInput =
    {
        Run = run
        Config = config
        Command = cmd
        Mode = mode
        TargetPackages = []
        ExtractPrevious = noPrevious
        ExtractCachedPrevious = noCachedPrevious
        ExtractCurrent = noCurrent
        CiPollIntervalMs = 0
        CiWait = CiWaitTests.fixedCiWait 0 10
        TagPush = immediateTagPush
        CheckFeedPresence = (fun _ _ -> OnFeed)
        CheckRestorable = (fun _ _ _ -> OnFeed)
        WaitForNuGet = false
        NuGetPollIntervalMs = 0
        NuGetMaxAttempts = 1
        Push = false
        Check = check
        Canary = noCanary
    }

/// A single-package repo at 1.0.0 with the given CHANGELOG body.
/// Returns (fsproj, ownDir, changelogPath, config).
let private seedSinglePackageRepo (rootDir: string) (changelogBody: string) =
    let srcDir = Path.Combine(rootDir, "src", "MyLib")
    Directory.CreateDirectory(srcDir) |> ignore
    let fsproj = Path.Combine(srcDir, "MyLib.fsproj")
    File.WriteAllText(fsproj, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")
    let changelog = Path.Combine(rootDir, "CHANGELOG.md")
    File.WriteAllText(changelog, changelogBody)

    let config =
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = fsproj
                        DllPath = "fake.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = rootDir
        }

    fsproj, Path.GetDirectoryName(fsproj), changelog, config

[<Fact>]
let ``release - derives the Unreleased section from commits when it is empty`` () =
    withTempDir (fun rootDir ->
        let fsproj, ownDir, changelog, config =
            seedSinglePackageRepo rootDir "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let descOut =
            "feat: shiny new capability"
            + rs
            + "fix: a subtle bug\n\nlong body that is dropped"
            + rs
            + "Bump versions: MyLib 1.0.0"
            + rs

        let (fakeRun, _) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + ownDir + "/**\"", Success "1 file changed")
                    ("jj", descArgsFor ownDir, Success descOut)
                ]

        let result = release (releaseInput fakeRun config StartAlpha PushTags false)

        test <@ result = 0 @>
        let updated = File.ReadAllText changelog
        test <@ updated.Contains "- feat: shiny new capability" @>
        test <@ updated.Contains "- fix: a subtle bug" @>
        test <@ not (updated.Contains "Bump versions") @>
        let featIdx = updated.IndexOf("- feat: shiny new capability")
        let fixIdx = updated.IndexOf("- fix: a subtle bug")
        test <@ featIdx < fixIdx @>
        test <@ not ((File.ReadAllText fsproj).Contains "<Version>1.0.0</Version>") @>)

[<Fact>]
let ``release - never clobbers a hand-authored Unreleased entry (derive skipped)`` () =
    withTempDir (fun rootDir ->
        let _fsproj, ownDir, changelog, config =
            seedSinglePackageRepo
                rootDir
                "# Changelog\n\n## Unreleased\n\n- feat: hand-written note\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let (fakeRun, _) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + ownDir + "/**\"", Success "1 file changed")
                    ("jj", descArgsFor ownDir, Success("feat: derived thing that must NOT appear" + rs))
                ]

        let result = release (releaseInput fakeRun config StartAlpha PushTags false)

        test <@ result = 0 @>
        let updated = File.ReadAllText changelog
        test <@ updated.Contains "- feat: hand-written note" @>
        test <@ not (updated.Contains "derived thing") @>)

[<Fact>]
let ``release - aborts before writes when Unreleased is empty and nothing is derivable`` () =
    withTempDir (fun rootDir ->
        let originalChangelog =
            "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let fsproj, ownDir, changelog, config =
            seedSinglePackageRepo rootDir originalChangelog

        let (fakeRun, _) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + ownDir + "/**\"", Success "1 file changed")
                    ("jj", descArgsFor ownDir, Success("Bump versions: MyLib 1.0.0" + rs))
                ]

        let result = release (releaseInput fakeRun config StartAlpha PushTags false)

        test <@ result = 1 @>
        test <@ (File.ReadAllText fsproj).Contains "<Version>1.0.0</Version>" @>
        test <@ File.ReadAllText changelog = originalChangelog @>)

/// fakeRun for `--check`: tag lookup, own-change diff and description log only.
let private checkRun (diffOut: string) (logOut: string) =
    fun (cmd: string) (args: string) ->
        match cmd, args with
        | "jj", a when a.StartsWith("tag list") -> Success "v1.0.0"
        | "jj", a when a.StartsWith("diff --from v1.0.0") -> Success diffOut
        | "jj", a when a.StartsWith("log -r \"v1.0.0..@\"") -> Success logOut
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

[<Fact>]
let ``release --check fails when a changed package has an empty non-derivable Unreleased`` () =
    withTempDir (fun rootDir ->
        let _fsproj, _ownDir, _changelog, config =
            seedSinglePackageRepo rootDir "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let run = checkRun "1 file changed" ("Bump versions: MyLib 1.0.0" + rs)
        let result = release (releaseInput run config Auto PushTags true)
        test <@ result = 1 @>)

[<Fact>]
let ``release --check passes when the empty Unreleased is derivable from commits`` () =
    withTempDir (fun rootDir ->
        let _fsproj, _ownDir, _changelog, config =
            seedSinglePackageRepo rootDir "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let run = checkRun "1 file changed" ("feat: derivable change" + rs)
        let result = release (releaseInput run config Auto PushTags true)
        test <@ result = 0 @>)

[<Fact>]
let ``release --check passes when the Unreleased entry is hand-authored`` () =
    withTempDir (fun rootDir ->
        let _fsproj, _ownDir, _changelog, config =
            seedSinglePackageRepo
                rootDir
                "# Changelog\n\n## Unreleased\n\n- feat: authored\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let run = checkRun "1 file changed" ("Bump versions: MyLib 1.0.0" + rs)
        let result = release (releaseInput run config Auto PushTags true)
        test <@ result = 0 @>)

[<Fact>]
let ``release --check passes when the changed package has no prior tag`` () =
    withTempDir (fun rootDir ->
        let _fsproj, _ownDir, _changelog, config =
            seedSinglePackageRepo rootDir "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let run =
            fun (cmd: string) (args: string) ->
                match cmd, args with
                | "jj", a when a.StartsWith("tag list") -> Success ""
                | "git", a when a.StartsWith("tag -l") -> Success ""
                | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let result = release (releaseInput run config Auto PushTags true)
        test <@ result = 0 @>)

[<Fact>]
let ``release --check passes when the package has no own-source changes since its tag`` () =
    withTempDir (fun rootDir ->
        let _fsproj, _ownDir, _changelog, config =
            seedSinglePackageRepo rootDir "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"

        let run = checkRun "" ""
        let result = release (releaseInput run config Auto PushTags true)
        test <@ result = 0 @>)

// --check and promotion read one plan, so the check prints what promotion writes.

let private fsprojWithRefs (refs: string) =
    sprintf "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup><ItemGroup>%s</ItemGroup></Project>" refs

let private pgvectorRefsAtTag =
    "<PackageReference Include=\"Microsoft.SourceLink.GitHub\" Version=\"10.0.301\" PrivateAssets=\"All\" /><PackageReference Include=\"SqlHydra.Query\" Version=\"4.1.0-beta.2\" />"

let private pgvectorRefsNow =
    "<PackageReference Include=\"Microsoft.SourceLink.GitHub\" Version=\"10.0.401\" PrivateAssets=\"All\" /><PackageReference Include=\"SqlHydra.Query\" Version=\"4.1.0-beta.3\" />"

let private pgvectorBumpBullet =
    "- build(deps): bump SqlHydra.Query from 4.1.0-beta.2 to 4.1.0-beta.3"

/// Dev-tooling and docs commits: all derivable, none authored.
let private pgvectorCommits =
    "chore(deps): bump our dev tools"
    + rs
    + "docs: trim thinking-out-loud comments"
    + rs
    + "style: restore the compact dotnet-tools.json layout"
    + rs

/// A single-package repo whose fsproj has `refsNow` (`refsAtTag` at v1.0.0) and
/// `commits` since the tag. Returns the run stub, the changelog path and config.
let private seedReleaseWithRefs (rootDir: string) (changelogBody: string) refsAtTag refsNow commits =
    let fsproj, ownDir, changelog, config = seedSinglePackageRepo rootDir changelogBody
    File.WriteAllText(fsproj, fsprojWithRefs refsNow)

    let (run, _) =
        passingCiRun
            [
                ("git", "tag -l \"v*\"", Success "v1.0.0")
                ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + ownDir + "/**\"", Success "1 file changed")
                ("jj", descArgsFor ownDir, Success commits)
                ("jj", sprintf "file show -r \"v1.0.0\" \"%s\"" fsproj, Success(fsprojWithRefs refsAtTag))
            ]

    run, changelog, config

let private promotedSection (changelog: string) =
    File.ReadAllLines changelog
    |> Array.skipWhile (fun l -> not (l.StartsWith "## Unreleased"))
    |> Array.skip 1
    |> Array.skipWhile (fun l -> not (l.StartsWith "## "))
    |> Array.skip 1
    |> Array.takeWhile (fun l -> not (l.StartsWith "## "))
    |> Array.filter (fun l -> l.Trim() <> "")
    |> Array.toList

/// The bullets `--check` said it would add.
let private announcedEntries (checkOutput: string) =
    checkOutput.Split('\n')
    |> Array.map (fun l -> l.Trim())
    |> Array.filter (fun l -> l.StartsWith "- ")
    |> Array.toList

[<Fact>]
let ``--check and release agree: an authored section plus an unauthored dependency bump`` () =
    withTempDir (fun rootDir ->
        let authored = "- chore: package metadata for OSS readiness"

        let run, changelog, config =
            seedReleaseWithRefs
                rootDir
                (sprintf "# Changelog\n\n## Unreleased\n\n%s\n\n## 1.0.0 - 2026-01-01\n\n- initial\n" authored)
                pgvectorRefsAtTag
                pgvectorRefsNow
                pgvectorCommits

        let checkOut, checkExit =
            withCapturedConsole (fun () -> release (releaseInput run config Auto PushTags true))

        test <@ checkExit = 0 @>
        test <@ checkOut.Contains "promoted as written" @>
        test <@ checkOut.Contains "commit summaries are not merged into an authored section" @>
        test <@ announcedEntries checkOut = [ pgvectorBumpBullet ] @>

        let releaseExit = release (releaseInput run config StartAlpha PushTags false)
        test <@ releaseExit = 0 @>

        let section = promotedSection changelog
        test <@ section = authored :: announcedEntries checkOut @>
        // A build-only SourceLink bump is not consumer-visible.
        test <@ not (File.ReadAllText(changelog).Contains "SourceLink") @>
        test <@ not (File.ReadAllText(changelog).Contains "trim thinking-out-loud") @>)

// Positive control: all authored, promoted exactly as written.
[<Fact>]
let ``--check and release agree: an all-authored release promotes unchanged`` () =
    withTempDir (fun rootDir ->
        let authored = [ "- feat: a real feature"; "- fix: a real fix" ]

        let run, changelog, config =
            seedReleaseWithRefs
                rootDir
                (sprintf
                    "# Changelog\n\n## Unreleased\n\n%s\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"
                    (String.concat "\n" authored))
                pgvectorRefsNow
                pgvectorRefsNow
                pgvectorCommits

        let checkOut, checkExit =
            withCapturedConsole (fun () -> release (releaseInput run config Auto PushTags true))

        test <@ checkExit = 0 @>
        test <@ checkOut.Contains "promoted as written" @>
        test <@ List.isEmpty (announcedEntries checkOut) @>

        test <@ release (releaseInput run config StartAlpha PushTags false) = 0 @>
        test <@ promotedSection changelog = authored @>)

[<Fact>]
let ``--check and release agree: an empty section is derived from commits and dependency changes`` () =
    withTempDir (fun rootDir ->
        let run, changelog, config =
            seedReleaseWithRefs
                rootDir
                "# Changelog\n\n## Unreleased\n\n## 1.0.0 - 2026-01-01\n\n- initial\n"
                pgvectorRefsAtTag
                pgvectorRefsNow
                ("Bump versions: MyLib 1.0.0" + rs)

        let checkOut, checkExit =
            withCapturedConsole (fun () -> release (releaseInput run config Auto PushTags true))

        // Only version-bump commits, but the dependency bump is still promotable.
        test <@ checkExit = 0 @>
        test <@ announcedEntries checkOut = [ pgvectorBumpBullet ] @>

        test <@ release (releaseInput run config StartAlpha PushTags false) = 0 @>
        test <@ promotedSection changelog = announcedEntries checkOut @>)

[<Fact>]
let ``fsprojsForChangelog - a multi-package changelog gets only the fsprojs beside it`` () =
    let pkg =
        {
            Name = "Alpha"
            Fsproj = "src/Alpha/Alpha.fsproj"
            DllPath = ""
            TagPrefix = "alpha-v"
            FsProjsSharingSameTag = [ "src/Alpha.Cli/Alpha.Cli.fsproj" ]
        }

    let config =
        {
            Packages =
                [
                    pkg
                    { pkg with
                        Name = "Beta"
                        Fsproj = "src/Beta/Beta.fsproj"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = "/repo"
        }

    test <@ fsprojsForChangelog config pkg "src/Alpha.Cli/CHANGELOG.md" = [ "src/Alpha.Cli/Alpha.Cli.fsproj" ] @>

    let single = { config with Packages = [ pkg ] }

    let expected = [ "src/Alpha/Alpha.fsproj"; "src/Alpha.Cli/Alpha.Cli.fsproj" ]
    test <@ fsprojsForChangelog single pkg "/repo/CHANGELOG.md" = expected @>

[<Fact>]
let ``dependencyChangesSinceTag - an fsproj unreadable at the tag or on disk derives nothing`` () =
    withTempDir (fun rootDir ->
        let fsproj = Path.Combine(rootDir, "New.fsproj")
        File.WriteAllText(fsproj, fsprojWithRefs pgvectorRefsNow)

        let config =
            {
                Packages = []
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = rootDir
            }

        let noHistory (_: string) (_: string) = Failure("no such path at tag", 1)
        test <@ List.isEmpty (dependencyChangesSinceTag noHistory config "v1.0.0" [ fsproj ]) @>

        let atTag (_: string) (_: string) =
            Success(fsprojWithRefs pgvectorRefsAtTag)

        test
            <@ List.isEmpty (dependencyChangesSinceTag atTag config "v1.0.0" [ Path.Combine(rootDir, "Gone.fsproj") ]) @>)

// A PackAsTool package skips the API probe (NU1212) but still gets its CLI grammar diffed.
[<Fact>]
let ``release - PackAsTool grammar break bumps major without constructing an API probe`` () =
    let dir =
        Path.Combine(scratchDir, "fsst-packastool-grammar-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore

    try
        let fsproj = Path.Combine(dir, "MyTool.fsproj")

        File.WriteAllText(
            fsproj,
            "<Project><PropertyGroup><Version>1.0.0</Version><PackAsTool>true</PackAsTool></PropertyGroup></Project>"
        )

        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + dir + "/**\"", Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyTool"
                            Fsproj = fsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun
                        Config = config
                        Command = Auto
                        Mode = PushTags
                        TargetPackages = []
                        // Restoring a PackAsTool package raises NU1212.
                        ExtractPrevious = mustNotRestore
                        ExtractCachedPrevious =
                            cachedWith (CachedUnreadable "API not read") (GrammarModelled checkApiGrammar)
                        ExtractCurrent = currentWith (fun _ -> []) (fun _ -> Some diffApiGrammar)
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        test <@ result = 0 @>
        test <@ output.Contains "note: MyTool: the CLI's env-var prefix is not a string literal" @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>2.0.0</Version>") @>
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``release - PackAsTool that is not a CommandTree CLI keeps the conservative NoChange bump``
    (grammarUnreadable: bool)
    =
    let current (_dll: string) : Extraction.ExtractedDll =
        {
            Api = Ok []
            Grammar =
                if grammarUnreadable then
                    GrammarUnreadable "could not load fake.dll: bad image"
                else
                    GrammarNotModellable "it is not a CommandTree consumer"
        }

    // No current grammar: not a CommandTree CLI, nothing to diff, so not fatal.
    let dir =
        Path.Combine(scratchDir, "fsst-packastool-nogrammar-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore

    try
        let fsproj = Path.Combine(dir, "MyTool.fsproj")

        File.WriteAllText(
            fsproj,
            "<Project><PropertyGroup><Version>1.0.0</Version><PackAsTool>true</PackAsTool></PropertyGroup></Project>"
        )

        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + dir + "/**\"", Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyTool"
                            Fsproj = fsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = mustNotRestore
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = current
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 0 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>1.0.1</Version>") @>
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact>]
let ``release - PackAsTool CLI aborts when the previous grammar cannot be read`` () =
    // A current grammar but no readable baseline (cold cache): refuse to guess, exit 1.
    let dir =
        Path.Combine(scratchDir, "fsst-packastool-coldcache-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore

    try
        let fsproj = Path.Combine(dir, "MyTool.fsproj")

        File.WriteAllText(
            fsproj,
            "<Project><PropertyGroup><Version>1.0.0</Version><PackAsTool>true</PackAsTool></PropertyGroup></Project>"
        )

        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"v*\"", Success "v1.0.0")
                    ("jj", "diff --from v1.0.0 --to @ --summary \"glob:" + dir + "/**\"", Success "1 file changed")
                ]

        let currentGrammar =
            {
                Roots = [ Leaf("diff-api", [], []) ]
                GlobalFlags = []
            }

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyTool"
                            Fsproj = fsproj
                            DllPath = "fake.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let result =
            release
                {
                    Run = fakeRun
                    Config = config
                    Command = Auto
                    Mode = PushTags
                    TargetPackages = []
                    ExtractPrevious = mustNotRestore
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = currentWith (fun _ -> []) (fun _ -> Some currentGrammar)
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush = immediateTagPush
                    CheckFeedPresence = (fun _ _ -> OnFeed)
                    CheckRestorable = (fun _ _ _ -> OnFeed)
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = noCanary
                }

        test <@ result = 1 @>
        test <@ (File.ReadAllText fsproj).Contains("<Version>1.0.0</Version>") @>
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

/// Release a PackAsTool CLI `Cli` at 1.0.0 (tag `cli-v1.0.0`, one own change since)
/// whose current build has a grammar, with the previous release read from the cache
/// as `previousGrammar` and `previousApi`, and the current API as `currentApi`.
/// Returns the output, the exit code and the fsproj's version afterwards.
let private releaseToolAgainst
    (previousGrammar: GrammarRead)
    (previousApi: string -> string -> CachedApi)
    (currentApi: Result<ApiSignature list, string>)
    =
    let dir =
        Path.Combine(scratchDir, "fsst-packastool-previous-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore

    try
        let fsproj = Path.Combine(dir, "Cli.fsproj")

        File.WriteAllText(
            fsproj,
            "<Project><PropertyGroup><Version>1.0.0</Version><PackAsTool>true</PackAsTool></PropertyGroup></Project>"
        )

        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let (fakeRun, _getCalls) =
            passingCiRun
                [
                    ("git", "tag -l \"cli-v*\"", Success "cli-v1.0.0")
                    ("jj", "diff --from cli-v1.0.0 --to @ --summary \"glob:" + dir + "/**\"", Success "1 file changed")
                ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "Cli"
                            Fsproj = fsproj
                            DllPath = "fake.dll"
                            TagPrefix = "cli-v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = fakeRun
                        Config = config
                        Command = Auto
                        Mode = PushTags
                        TargetPackages = []
                        ExtractPrevious = mustNotRestore
                        ExtractCachedPrevious =
                            fun pkg version ->
                                {
                                    Api = previousApi pkg version
                                    Grammar = previousGrammar
                                }
                        ExtractCurrent =
                            fun _ ->
                                {
                                    Api = currentApi
                                    Grammar = GrammarModelled checkApiGrammar
                                }
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 10
                        TagPush = immediateTagPush
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary
                    })

        output, result, File.ReadAllText fsproj
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact>]
let ``release - PackAsTool CLI whose previous package is missing fails closed with an installable fix`` () =
    let missing =
        GrammarUnreadable "Cli 1.0.0 is not in the NuGet cache at /home/me/.nuget/packages"

    let output, result, fsproj =
        releaseToolAgainst missing (fun _ _ -> NotCached) (Ok [])

    test <@ result = 1 @>
    test <@ fsproj.Contains "<Version>1.0.0</Version>" @>

    test
        <@
            output.Contains
                "could not read the CLI grammar of the previous release cli-v1.0.0: Cli 1.0.0 is not in the NuGet cache"
        @>

    // The version, not the tag: `dotnet tool install` rejects `--version cli-v1.0.0`.
    test <@ output.Contains "dotnet tool install --tool-path <tmp> Cli --version 1.0.0`" @>
    test <@ not (output.Contains "--version cli-v") @>

[<Fact>]
let ``release - PackAsTool CLI whose previous grammar cannot be modelled is bumped by its API diff`` () =
    let notModellable =
        GrammarNotModellable
            "it has 4 candidate root command unions (A, B, C, D), so which one is the CLI cannot be told"

    let output, result, fsproj =
        releaseToolAgainst
            notModellable
            (fun _ version ->
                if version = "1.0.0" then
                    CachedRead [ ApiSignature.TypeDecl "Cli" ]
                else
                    failwith "wrong baseline")
            (Ok [ ApiSignature.TypeDecl "Cli"; ApiSignature.TypeDecl "Cli.Added" ])

    test <@ result = 0 @>

    test
        <@
            output.Contains
                "note: Cli: the CLI grammar of the previous release cli-v1.0.0 could not be modelled (it has 4 candidate root command unions (A, B, C, D), so which one is the CLI cannot be told), so the API diff alone decides the bump"
        @>

    test <@ output.Contains "Bumping Cli: own change to a PackAsTool package — public API diffed since cli-v1.0.0" @>
    test <@ not (output.Contains "is not in the NuGet cache") @>
    test <@ fsproj.Contains "<Version>1.1.0</Version>" @>

[<Fact>]
let ``release - PackAsTool CLI with an unmodellable grammar and an unreadable API fails closed`` () =
    let output, result, fsproj =
        releaseToolAgainst
            (GrammarNotModellable "it has no root command union")
            (fun _ _ -> CachedUnreadable "could not load Cli.dll")
            (Ok [])

    test <@ result = 1 @>
    test <@ fsproj.Contains "<Version>1.0.0</Version>" @>

    test
        <@
            output.Contains
                "the CLI grammar of the previous release cli-v1.0.0 could not be modelled, and its public API could not be read either (could not load Cli.dll)"
        @>

[<Fact>]
let ``release - PackAsTool CLI with an unmodellable grammar and an uncached API fails closed`` () =
    let output, result, fsproj =
        releaseToolAgainst (GrammarNotModellable "it has no root command union") (fun _ _ -> NotCached) (Ok [])

    test <@ result = 1 @>
    test <@ fsproj.Contains "<Version>1.0.0</Version>" @>

    test
        <@
            output.Contains
                "the CLI grammar of the previous release cli-v1.0.0 could not be modelled, and its public API could not be read either (it is not in the NuGet cache)"
        @>

[<Fact>]
let ``release - PackAsTool CLI with an unmodellable grammar and an unreadable current API fails closed`` () =
    let output, result, fsproj =
        releaseToolAgainst
            (GrammarNotModellable "it has no root command union")
            (fun _ _ -> CachedRead [ ApiSignature.TypeDecl "Cli" ])
            (Error "could not load fake.dll: bad image")

    test <@ result = 1 @>
    test <@ fsproj.Contains "<Version>1.0.0</Version>" @>

    test
        <@
            output.Contains
                "Cli: could not read the public API of the current build (could not load fake.dll: bad image)"
        @>

// A merge can push the `## Unreleased` callout below new entries; --check fails on
// that, and derivable commits never suppress it.

let private calloutPkg (dir: string) (name: string) : PackageConfig =
    {
        Name = name
        Fsproj = Path.Combine(dir, name, name + ".fsproj")
        DllPath = ""
        TagPrefix = name.ToLowerInvariant() + "-v"
        FsProjsSharingSameTag = []
    }

let private writeChangelog (dir: string) (name: string) (text: string) =
    let sub = Path.Combine(dir, name)
    Directory.CreateDirectory sub |> ignore
    File.WriteAllText(Path.Combine(sub, "CHANGELOG.md"), text)

let private calloutFirstText =
    "# Changelog\n\n## Unreleased\n\n> ### Read this first\n>\n> It breaks scripts.\n\n- fix: thing\n"

let private calloutBuriedText =
    "# Changelog\n\n## Unreleased\n\n- fix: thing\n\n> ### Read this first\n>\n> It breaks scripts.\n"

let private noTags (cmd: string) (args: string) : CommandResult =
    match cmd, args with
    | "git", a when a.StartsWith("tag -l") -> Success ""
    | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

let private runCheck (config: ToolConfig) =
    release
        {
            Run = noTags
            Config = config
            Command = Auto
            Mode = DryRun
            TargetPackages = []
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 1
            TagPush = immediateTagPush
            CheckFeedPresence = (fun _ _ -> OnFeed)
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = false
            Check = true
            Canary = noCanary
        }

[<Fact>]
let ``calloutCheckPaths - multi-package repo also covers the repo-root changelog`` () =
    withTempDir (fun dir ->
        let config =
            {
                Packages = [ calloutPkg dir "Alpha"; calloutPkg dir "Beta" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let paths = calloutCheckPaths config config.Packages |> List.map snd

        test <@ paths |> List.contains (Path.Combine(dir, "Alpha", "CHANGELOG.md")) @>
        test <@ paths |> List.contains (Path.Combine(dir, "Beta", "CHANGELOG.md")) @>
        test <@ paths |> List.contains (Path.Combine(dir, "CHANGELOG.md")) @>)

[<Fact>]
let ``calloutCheckPaths - single-package repo lists the root changelog once, under the package`` () =
    withTempDir (fun dir ->
        let config =
            {
                Packages = [ calloutPkg dir "Solo" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let paths = calloutCheckPaths config config.Packages
        test <@ paths = [ "Solo", Path.Combine(dir, "CHANGELOG.md") ] @>)

[<Fact>]
let ``release --check - fails when a package changelog buries its callout`` () =
    withTempDir (fun dir ->
        writeChangelog dir "Alpha" calloutBuriedText

        let config =
            {
                Packages = [ calloutPkg dir "Alpha"; calloutPkg dir "Beta" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        test <@ runCheck config = 1 @>)

// Positive control: callout first passes.
[<Fact>]
let ``release --check - passes when the callout leads the section`` () =
    withTempDir (fun dir ->
        writeChangelog dir "Alpha" calloutFirstText

        let config =
            {
                Packages = [ calloutPkg dir "Alpha"; calloutPkg dir "Beta" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        test <@ runCheck config = 0 @>)

[<Fact>]
let ``release --check - fails when the repo-root aggregate buries its callout`` () =
    withTempDir (fun dir ->
        // Per-package changelogs are fine; the root aggregate is the one that got merged.
        writeChangelog dir "Alpha" calloutFirstText
        File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), calloutBuriedText)

        let config =
            {
                Packages = [ calloutPkg dir "Alpha"; calloutPkg dir "Beta" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        test <@ runCheck config = 1 @>)

[<Fact>]
let ``calloutOrderProblems - names the package and the buried callout`` () =
    withTempDir (fun dir ->
        writeChangelog dir "Alpha" calloutBuriedText

        let config =
            {
                Packages = [ calloutPkg dir "Alpha"; calloutPkg dir "Beta" ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = dir
            }

        let problems = calloutOrderProblems config config.Packages

        test
            <@
                problems =
                    [
                        "Alpha",
                        Changelog.CalloutNotFirst(Path.Combine(dir, "Alpha", "CHANGELOG.md"), "Read this first", 7)
                    ]
            @>)


/// `passingCiRun`, but the tag-run query answers `[]` for `emptyRounds` rounds, then a queued run.
let private ciRunWithLateTagRun (emptyRounds: int) =
    let (base', getCalls) = passingCiRun []
    let mutable tagAsks = 0

    let run (cmd: string) (args: string) =
        match cmd, args with
        | "gh", a when a.StartsWith("run list --branch") ->
            tagAsks <- tagAsks + 1

            if tagAsks <= emptyRounds then
                Success "[]"
            else
                Success
                    """[{"name":"Release","status":"queued","conclusion":null,"databaseId":1,"url":"https://example/1"}]"""
        | _ -> base' cmd args

    run, getCalls

let private singlePackage (tmpFile: string) : ToolConfig =
    {
        Packages =
            [
                {
                    Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = []
                }
            ]
        ReservedVersions = Set.empty
        PreBuildCmds = []
        PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
        CiTimeout = None
        RootDir = ""
    }

let private releaseWithTagPush run config (policy: TagPushPolicy) =
    seedTmpChangelog ()

    release
        {
            Run = run
            Config = { config with RootDir = scratchDir }
            Command = StartAlpha
            Mode = PushTags
            TargetPackages = []
            ExtractPrevious = noPrevious
            ExtractCachedPrevious = noCachedPrevious
            ExtractCurrent = noCurrent
            CiPollIntervalMs = 0
            CiWait = CiWaitTests.fixedCiWait 0 10
            TagPush = policy
            CheckFeedPresence = (fun _ _ -> OnFeed)
            CheckRestorable = (fun _ _ _ -> OnFeed)
            WaitForNuGet = false
            NuGetPollIntervalMs = 0
            NuGetMaxAttempts = 1
            Push = false
            Check = false
            Canary = noCanary
        }

[<Fact>]
let ``release - a Release run that registers a few polls after the push exits 0 and is never MISSING`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (run, _) = ciRunWithLateTagRun 3

        let output, result =
            withCapturedConsole (fun () ->
                releaseWithTagPush
                    run
                    (singlePackage tmpFile)
                    {
                        PushAttempts = 1
                        PushRetryDelayMs = 0
                        RunPollIntervalMs = 0
                        RunPollAttempts = 10
                    })

        test <@ result = 0 @>
        test <@ not (output.Contains("MISSING TRIGGER")) @>
        test <@ not (output.Contains("no workflow run YET")) @>
        test <@ output.Contains("a workflow run exists for each") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - a Release run that never appears within the budget is reported, without re-push advice`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (run, _) = ciRunWithLateTagRun System.Int32.MaxValue

        let output, result =
            withCapturedConsole (fun () ->
                releaseWithTagPush
                    run
                    (singlePackage tmpFile)
                    {
                        PushAttempts = 1
                        PushRetryDelayMs = 0
                        RunPollIntervalMs = 0
                        RunPollAttempts = 3
                    })

        test <@ result = 2 @>
        test <@ output.Contains("ARE on the remote") @>
        test <@ output.Contains("Do NOT delete and re-push") @>
        test <@ output.Contains("may still be starting") @>
        test <@ not (output.Contains("MISSING TRIGGER")) @>
        test <@ not (output.Contains(":refs/tags/")) @>
    finally
        File.Delete(tmpFile)


[<Fact>]
let ``nuGetPollFromEnv - the default budget covers twenty minutes of index lag`` () =
    // Packages can index 6-15 minutes after the Release run finishes.
    let intervalMs, attempts = nuGetPollFromEnv (fun _ -> None)
    test <@ int64 (attempts - 1) * int64 intervalMs >= 20L * 60L * 1000L @>

[<Fact>]
let ``nuGetPollFromEnv - honours the same overrides as FsHotWatch's barrier`` () =
    let intervalMs, attempts =
        nuGetPollFromEnv (function
            | "FSHW_NUGET_PROBE_ATTEMPTS" -> Some "5"
            | "FSHW_NUGET_PROBE_DELAY_MS" -> Some "100"
            | _ -> None)

    test <@ attempts = 5 @>
    test <@ intervalMs = 100 @>

    let _, garbageAttempts = nuGetPollFromEnv (fun _ -> Some "soon")
    let _, defaultAttempts = nuGetPollFromEnv (fun _ -> None)
    test <@ garbageAttempts = defaultAttempts @>

[<Fact>]
let ``waitForNuGetOn - the give-up names the measured wait, not the budget`` () =
    // 3 attempts 100ms apart sleep twice: 200ms, not the 300ms budget. The clock is the
    // test's, so the answer is exact however loaded the machine is.
    let mutable now = System.TimeSpan.Zero

    let sleep (ms: int) =
        now <- now + System.TimeSpan.FromMilliseconds(float ms)

    let output, (unconfirmed, waited) =
        withCapturedConsole (fun () ->
            waitForNuGetOn sleep (fun () -> now) (fun _ _ -> NotOnFeed) 100 3 [ "PkgA", "1.0.0" ])

    test <@ unconfirmed = [ "PkgA", "1.0.0" ] @>
    test <@ waited = System.TimeSpan.FromMilliseconds 200.0 @>
    test <@ output.Contains("Gave up waiting for PkgA 1.0.0 on NuGet after 0.2s (3 checks") @>

[<Fact>]
let ``waitForNuGetTimed - measures the wait on the wall clock`` () =
    let _, (unconfirmed, waited) =
        withCapturedConsole (fun () -> waitForNuGetTimed (fun _ _ -> NotOnFeed) 50 2 [ "PkgA", "1.0.0" ])

    test <@ unconfirmed = [ "PkgA", "1.0.0" ] @>
    // One 50ms sleep: a floor only, since a loaded machine can only make it longer.
    test <@ waited >= System.TimeSpan.FromMilliseconds 50.0 @>

[<Fact>]
let ``release - the NuGet give-up says the tags and Release runs are the evidence and a re-run resumes`` () =
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _) = passingCiRun []

        let output, result =
            withCapturedConsole (fun () ->
                runReleaseWithNuGetWait fakeRun (singlePackage tmpFile) StartAlpha (fun _ _ -> NotOnFeed) 2)

        test <@ result = 2 @>
        test <@ output.Contains("Release NOT CONFIRMED") @>
        test <@ output.Contains("stopped waiting after ") @>
        test <@ output.Contains("The tags ARE pushed and each has a Release run") @>
        test <@ output.Contains("Re-running the same release command RESUMES") @>
        test <@ output.Contains("does not publish a second time") @>
        test <@ output.Contains("FSHW_NUGET_PROBE_ATTEMPTS") @>
        test <@ output.Contains("This is NOT a failed publish") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``formatElapsed - minutes and seconds under a minute read as an operator expects`` () =
    test <@ formatElapsed (System.TimeSpan.FromSeconds 252.0) = "4m12s" @>
    test <@ formatElapsed (System.TimeSpan.FromSeconds 65.0) = "1m05s" @>
    test <@ formatElapsed (System.TimeSpan.FromMilliseconds 300.0) = "0.3s" @>

[<Fact>]
let ``pollBudget - N checks spend N-1 sleeps, and a zero-check poll is a zero budget`` () =
    // 81 checks 15s apart is 20m, not 20m15s; 0 attempts must not go negative.
    test <@ pollBudget 15000 81 = System.TimeSpan.FromMinutes 20.0 @>
    test <@ pollBudget 15000 1 = System.TimeSpan.Zero @>
    test <@ pollBudget 15000 0 = System.TimeSpan.Zero @>

[<Fact>]
let ``a failed publish run still reads as a sentence when GitHub reports no name and no url`` () =
    // gh can return an empty run name or url; print neither as " ()".
    let output, result =
        withCapturedConsole (fun () ->
            reportTagConfirmationFailures
                [
                    TagConfirmationFailure.WorkflowRunFailed(
                        "fssemantictagger-v0.14.0-alpha.8",
                        [
                            {
                                Workflow = PublishWorkflow ".github/workflows/release.yml"
                                Name = ""
                                Url = ""
                                RunId = "42"
                                Status = Completed
                                Conclusion = FailureConclusion
                            }
                        ]
                    )
                ])

    test <@ result = 1 @>
    test <@ output.Contains("publish workflow .github/workflows/release.yml finished") @>
    test <@ output.Contains("no url reported") @>
    test <@ not (output.Contains("release.yml ()")) @>

[<Fact>]
let ``release - a preBuildCmd with no arguments runs with an empty argument string`` () =
    // A bare command name splits into one part.
    let tmpFile = scratchFile ()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, getCalls) =
            passingCiRun [ ("restore-tools", "", Success "Restored.") ]

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = tmpFile
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                            TagPrefix = "v"
                            FsProjsSharingSameTag = []
                        }
                    ]
                ReservedVersions = Set.empty
                PreBuildCmds = [ "restore-tools" ]
                PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
                CiTimeout = None
                RootDir = ""
            }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        let calls = getCalls ()
        test <@ result = 0 @>

        let preBuildIdx =
            calls |> List.findIndex (fun (c, a) -> c = "restore-tools" && a = "")

        let buildIdx =
            calls |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "build -c Release")

        test <@ preBuildIdx < buildIdx @>
    finally
        File.Delete(tmpFile)


/// One tag (`core-v`) shipping `src/Core` and the PackAsTool `src/Cli`, which alone
/// uses `src/CliHelper`; `src/Unrelated` is in no closure. Returns the primary fsproj and config.
let private writeSharedTagRepo (root: string) =
    let dirOf name = Path.Combine(root, "src", name)

    for name in [ "Core"; "Cli"; "CliHelper"; "Unrelated"; "Other" ] do
        Directory.CreateDirectory(dirOf name) |> ignore

    let fsproj name =
        Path.Combine(dirOf name, name + ".fsproj")

    let project (props: string) (refs: string list) =
        let items =
            refs
            |> List.map (sprintf "    <ProjectReference Include=\"%s\" />")
            |> String.concat "\n"

        sprintf
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>%s</PropertyGroup>\n  <ItemGroup>\n%s\n  </ItemGroup>\n</Project>"
            props
            items

    File.WriteAllText(fsproj "Core", project "<Version>2.0.0</Version>" [])

    File.WriteAllText(
        fsproj "Cli",
        project
            "<Version>2.0.0</Version><PackAsTool>true</PackAsTool>"
            [ "../Core/Core.fsproj"; "../CliHelper/CliHelper.fsproj" ]
    )

    File.WriteAllText(fsproj "CliHelper", project "" [])
    File.WriteAllText(fsproj "Unrelated", project "" [])
    File.WriteAllText(fsproj "Other", project "<Version>1.0.0</Version>" [])

    for name in [ "Core"; "Cli" ] do
        File.WriteAllText(Path.Combine(dirOf name, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- fix: a\n")

    let config =
        {
            Packages =
                [
                    {
                        Name = "Core"
                        Fsproj = fsproj "Core"
                        DllPath = "fake.dll"
                        TagPrefix = "core-v"
                        FsProjsSharingSameTag = [ fsproj "Cli" ]
                    }
                    {
                        Name = "Other"
                        Fsproj = fsproj "Other"
                        DllPath = "fake.dll"
                        TagPrefix = "other-v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = FsSemanticTagger.Config.defaultPublishWorkflows
            CiTimeout = None
            RootDir = root
        }

    fsproj "Core", config

/// A run where the only change since `core-v2.0.0` is under `changedDir`. Every
/// other `jj diff --from` is answered explicitly: a failure reads as "changed".
let private sharedTagRun (changedDir: string) =
    let fakeRun, _calls = passingCiRun []

    fun (cmd: string) (args: string) ->
        if cmd = "git" && args = "tag -l \"core-v*\"" then
            Success "core-v2.0.0"
        elif cmd = "jj" && args.StartsWith "diff --from core-v2.0.0" then
            if args.Contains(changedDir + "/**") then
                Success("M " + changedDir + "/Program.fs")
            else
                Success ""
        else
            fakeRun cmd args

[<Fact>]
let ``packageChangeDirs includes every fsProjsSharingSameTag project and its ProjectReference closure`` () =
    withTempDir (fun root ->
        let _, config = writeSharedTagRepo root
        let core = config.Packages.Head
        let dirs = packageChangeDirs config core |> List.map (fun d -> d.Replace('\\', '/'))

        let endsWith (suffix: string) =
            dirs |> List.exists (fun d -> d.EndsWith suffix)

        test <@ endsWith "src/Core" @>
        test <@ endsWith "src/Cli" @>
        test <@ endsWith "src/CliHelper" @>
        test <@ not (endsWith "src/Unrelated") @>
        test <@ not (endsWith "src/Other") @>)

[<Fact>]
let ``release - Auto releases a patch when only a fsProjsSharingSameTag project changed`` () =
    // Only the CLI sharing the tag changed: it must release, as a patch.
    withTempDir (fun root ->
        let coreFsproj, config = writeSharedTagRepo root

        let output, result =
            releaseWithUnchangedApi (sharedTagRun (Path.Combine(root, "src", "Cli"))) config [ "Core" ]

        test <@ result = 0 @>
        test <@ not (output.Contains "Skipping Core") @>
        test <@ (File.ReadAllText coreFsproj).Contains("<Version>2.0.1</Version>") @>)

[<Fact>]
let ``release - Auto releases a patch when only a sharing project's bundled reference changed`` () =
    withTempDir (fun root ->
        let coreFsproj, config = writeSharedTagRepo root

        let output, result =
            releaseWithUnchangedApi (sharedTagRun "src/CliHelper") config [ "Core" ]

        test <@ result = 0 @>
        test <@ not (output.Contains "Skipping Core") @>
        test <@ (File.ReadAllText coreFsproj).Contains("<Version>2.0.1</Version>") @>)

[<Fact>]
let ``release - Auto still skips a package when the change is outside every closure behind its tag`` () =
    withTempDir (fun root ->
        let coreFsproj, config = writeSharedTagRepo root

        let output, result =
            releaseWithUnchangedApi (sharedTagRun "src/Unrelated") config [ "Core" ]

        test <@ result = 0 @>
        test <@ output.Contains "Skipping Core: no changes since core-v2.0.0" @>
        test <@ (File.ReadAllText coreFsproj).Contains("<Version>2.0.0</Version>") @>)

/// A release whose base is not, or stops being, `main@origin`.
///
/// Observed 2026-11-02 in the FsHotWatch release: another agent landed a commit
/// on main during the 25-minute consumer canary, `jj bookmark set main -r @-` then
/// refused to move main sideways, and the release died with an unhandled
/// exception, nothing tagged and a stray bump commit left behind.
module FsSemanticTagger.Tests.MainMovedTests

open System.IO
open Xunit
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Release
open FsSemanticTagger.Api
open FsSemanticTagger.Vcs
open FsSemanticTagger.Tests.ExtractionFakes

/// What `main@origin` looks like at the post-canary fetch, and how jj answers the
/// writes after it.
type private Scenario =
    {
        /// Commits the release base lacks at the FIRST fetch (before CI and canary).
        MissingBefore: string
        /// Commits the release base lacks at the SECOND fetch (after the canary).
        MissingAfter: string
        /// The paths those later commits change.
        ChangedPaths: string
        /// Whether the release base is an ancestor of the new main.
        Diverged: bool
        FirstFetch: CommandResult
        SecondFetch: CommandResult
        Commit: CommandResult
        BookmarkSet: CommandResult
        Abandon: CommandResult
        GitPush: CommandResult
    }

let private calm =
    {
        MissingBefore = ""
        MissingAfter = ""
        ChangedPaths = ""
        Diverged = false
        FirstFetch = Success ""
        SecondFetch = Success ""
        Commit = Success ""
        BookmarkSet = Success ""
        Abandon = Success ""
        GitPush = Success ""
    }

let private landed = "deadbeef0001 feat: someone else's change\u001e"

let private noCanary (dir: string) : ConsumerCanary.Settings =
    {
        ConfigPath = Path.Combine(dir, "no-such-fssemantictagger.json")
        Skip = false
        LogDir = Path.Combine(dir, "canary-logs")
        ScratchDir = Path.Combine(dir, "canary-seed")
        PackagesCache = Path.Combine(dir, "canary-cache")
        VerdictDir = Path.Combine(dir, "canary-cache-verdicts")
        Ops =
            {
                RunIn = fun _ cmd _ -> failwithf "unexpected canary process: %s" cmd
                RunGate = fun _ command _ _ -> failwithf "unexpected canary gate: %s" command
            }
    }

/// Run a StartAlpha / PushTags release of one package laid out as a real repo
/// (`src/MyLib/MyLib.fsproj` under `RootDir`), with jj answering per `scenario`.
/// Returns the output, the exit code, every command run, and the fsproj after.
let private releaseUnder (scenario: Scenario) : string * int * (string * string) list * string =
    withTempDir (fun root ->
        let libDir = Path.Combine(root, "src", "MyLib")
        Directory.CreateDirectory libDir |> ignore
        let fsproj = Path.Combine(libDir, "MyLib.fsproj")

        File.WriteAllText(
            fsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>"
        )

        File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- test entry\n")

        let mutable calls = []
        let mutable fetches = 0

        let run (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "git fetch" ->
                fetches <- fetches + 1

                if fetches = 1 then
                    scenario.FirstFetch
                else
                    scenario.SecondFetch
            | "jj", a when a.StartsWith("log -r \"@-..main@origin\"") ->
                Success(
                    if fetches <= 1 then
                        scenario.MissingBefore
                    else
                        scenario.MissingAfter
                )
            | "jj", "diff --from @- --to main@origin --name-only" -> Success scenario.ChangedPaths
            | "jj", a when a.Contains("~ ::main@origin") -> Success(if scenario.Diverged then "abc" else "")
            | "jj", "new main@origin" -> Success ""
            | "jj", a when a.StartsWith("abandon") -> scenario.Abandon
            | "jj", "diff --summary" -> Success ""
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "bump0123456789"
            | "jj", a when a.Contains("remote_bookmarks()") -> Success "parent1"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", a when a.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> scenario.Commit
            | "jj", a when a.StartsWith("bookmark set") -> scenario.BookmarkSet
            | "jj", "git push" -> scenario.GitPush
            | "jj", "git export" -> Success ""
            | "git", a when a.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

        let config =
            {
                Packages =
                    [
                        {
                            Name = "MyLib"
                            Fsproj = fsproj
                            DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
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

        let output, result =
            withCapturedConsole (fun () ->
                release
                    {
                        Run = run
                        Config = config
                        Command = StartAlpha
                        Mode = PushTags
                        TargetPackages = []
                        ExtractPrevious = noPrevious
                        ExtractCachedPrevious = noCachedPrevious
                        ExtractCurrent = noCurrent
                        CiPollIntervalMs = 0
                        CiWait = CiWaitTests.fixedCiWait 0 3
                        TagPush =
                            {
                                PushAttempts = 1
                                PushRetryDelayMs = 0
                                RunPollIntervalMs = 0
                                RunPollAttempts = 1
                            }
                        CheckFeedPresence = (fun _ _ -> OnFeed)
                        CheckRestorable = (fun _ _ _ -> OnFeed)
                        WaitForNuGet = false
                        NuGetPollIntervalMs = 0
                        NuGetMaxAttempts = 1
                        Push = false
                        Check = false
                        Canary = noCanary root
                    })

        output, result, calls, File.ReadAllText fsproj)

let private ran (calls: (string * string) list) (prefix: string) =
    calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith(prefix))

let private indexOf (calls: (string * string) list) (prefix: string) =
    calls |> List.findIndex (fun (c, a) -> c = "jj" && a.StartsWith(prefix))

[<Fact>]
let ``a release whose base is behind remote main is refused before CI and canary, naming the new commits`` () =
    let output, result, calls, fsproj =
        releaseUnder { calm with MissingBefore = landed }

    test <@ result = 1 @>
    test <@ output.Contains("deadbeef0001") @>
    test <@ output.Contains("feat: someone else's change") @>
    test <@ output.Contains("jj new main@origin") @>
    // Fail FAST: no CI wait, nothing written.
    test <@ not (calls |> List.exists (fun (c, _) -> c = "gh")) @>
    test <@ not (ran calls "commit") @>
    test <@ fsproj.Contains("<Version>0.0.0</Version>") @>

[<Fact>]
let ``a release that cannot fetch before CI is refused rather than run on an unknown base`` () =
    let output, result, calls, _ =
        releaseUnder
            { calm with
                FirstFetch = Failure("Error: could not resolve host", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("could not resolve host") @>
    test <@ not (calls |> List.exists (fun (c, _) -> c = "gh")) @>
    test <@ not (ran calls "commit") @>

[<Fact>]
let ``main moving during the canary by commits that touch a released package refuses cleanly, writing nothing`` () =
    let output, result, calls, fsproj =
        releaseUnder
            { calm with
                MissingAfter = landed
                ChangedPaths = "src/MyLib/Lib.fs\ndocs/guide.md\n"
            }

    test <@ result = 1 @>
    test <@ output.Contains("deadbeef0001") @>
    test <@ output.Contains("src/MyLib/Lib.fs") @>
    test <@ output.Contains("re-run from the new main") @>
    test <@ not (ran calls "commit") @>
    test <@ not (ran calls "new main@origin") @>
    test <@ not (ran calls "tag set") @>
    test <@ fsproj.Contains("<Version>0.0.0</Version>") @>

[<Fact>]
let ``main moving during the canary by a shared build input is treated as touching every package`` () =
    let output, result, calls, _ =
        releaseUnder
            { calm with
                MissingAfter = landed
                ChangedPaths = "Directory.Build.props\n"
            }

    test <@ result = 1 @>
    test <@ output.Contains("Directory.Build.props") @>
    test <@ not (ran calls "commit") @>

[<Fact>]
let ``main moving during the canary by unrelated commits moves the release onto the new main and completes`` () =
    let output, result, calls, fsproj =
        releaseUnder
            { calm with
                MissingAfter = landed
                ChangedPaths = "docs/guide.md\nsrc/Other/Other.fs\n"
            }

    test <@ result = 0 @>
    test <@ output.Contains("deadbeef0001") @>
    test <@ output.Contains("touch none of the released packages") @>
    test <@ ran calls "new main@origin" @>
    test <@ indexOf calls "new main@origin" < indexOf calls "commit" @>
    test <@ ran calls "tag set --allow-move v0.1.0-alpha.1" @>
    test <@ fsproj.Contains("<Version>0.1.0-alpha.1</Version>") @>

[<Fact>]
let ``main that diverged from the release base during the canary is refused, never rebased onto`` () =
    let _, result, calls, _ =
        releaseUnder
            { calm with
                MissingAfter = landed
                ChangedPaths = "docs/guide.md\n"
                Diverged = true
            }

    test <@ result = 1 @>
    test <@ not (ran calls "new main@origin") @>
    test <@ not (ran calls "commit") @>

[<Fact>]
let ``a failed fetch after the canary refuses with nothing written`` () =
    let output, result, calls, fsproj =
        releaseUnder
            { calm with
                SecondFetch = Failure("Error: could not resolve host", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("could not resolve host") @>
    test <@ not (ran calls "commit") @>
    test <@ fsproj.Contains("<Version>0.0.0</Version>") @>

[<Fact>]
let ``a refused main move abandons the unpushed bump commit instead of crashing`` () =
    let output, result, calls, _ =
        releaseUnder
            { calm with
                BookmarkSet = Failure("Error: Refusing to move bookmark backwards or sideways: main", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("Refusing to move bookmark backwards or sideways") @>
    test <@ ran calls "abandon bump0123456789" @>
    test <@ output.Contains("abandoned") @>
    test <@ not (ran calls "git push") @>
    test <@ not (ran calls "tag set") @>

[<Fact>]
let ``a refused main move whose bump commit cannot be abandoned says exactly what was left`` () =
    let output, result, _, _ =
        releaseUnder
            { calm with
                BookmarkSet = Failure("Error: Refusing to move bookmark backwards or sideways: main", 1)
                Abandon = Failure("Error: some jj failure", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("bump0123456789") @>
    test <@ output.Contains("jj abandon bump0123456789") @>
    test <@ output.Contains("some jj failure") @>

[<Fact>]
let ``touchingRelease - directory entries match whole path segments, the root entry matches everything`` () =
    test
        <@
            MainFreshness.touchingRelease [ "src/MyLib" ] [ "src/MyLibrary/X.fs"; "src/MyLib/X.fs" ] =
                [ "src/MyLib/X.fs" ]
        @>

    test
        <@ MainFreshness.touchingRelease [ "CHANGELOG.md" ] [ "CHANGELOG.md"; "docs/CHANGELOG.md" ] = [ "CHANGELOG.md" ] @>

    test <@ MainFreshness.touchingRelease [ "" ] [ "docs/a.md" ] = [ "docs/a.md" ] @>

[<Fact>]
let ``touchingRelease - shared build inputs touch the release wherever they sit`` () =
    let changed =
        [
            "build/Common.targets"
            "global.json"
            "NuGet.Config"
            "src/Other/Directory.Build.props"
            "docs/a.md"
        ]

    test
        <@
            MainFreshness.touchingRelease [] changed =
                [
                    "build/Common.targets"
                    "global.json"
                    "NuGet.Config"
                    "src/Other/Directory.Build.props"
                ]
        @>

[<Fact>]
let ``a failed bump commit refuses without touching main, and says the edits are uncommitted`` () =
    let output, result, calls, _ =
        releaseUnder
            { calm with
                Commit = Failure("Error: the working copy is stale", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("the working copy is stale") @>
    test <@ output.Contains("uncommitted") @>
    test <@ not (ran calls "bookmark set") @>
    test <@ not (ran calls "abandon") @>

/// A jj that answers `answers` (by argument prefix) and fails everything else.
let private jjAnswering (answers: (string * CommandResult) list) (_: string) (args: string) : CommandResult =
    answers
    |> List.tryFind (fun (prefix, _) -> args.StartsWith(prefix: string))
    |> Option.map snd
    |> Option.defaultValue (Failure(sprintf "unexpected: %s" args, 1))

let private missingQuery = "log -r \"@-..main@origin\""
let private ancestorQuery = "log -r \"@- ~ ::main@origin\""
let private pathsQuery = "diff --from @- --to main@origin"

[<Fact>]
let ``confirmBaseIsMain - an unreadable comparison refuses, naming jj's answer`` () =
    let run =
        jjAnswering [ "git fetch", Success ""; missingQuery, Failure("no main@origin", 1) ]

    match MainFreshness.confirmBaseIsMain run with
    | Error reason -> test <@ reason.Contains("no main@origin") && reason.Contains("Nothing was written") @>
    | Ok() -> failwith "an unreadable comparison must refuse"

[<Fact>]
let ``reconcileAfterCanary - an unreadable comparison refuses`` () =
    let run =
        jjAnswering [ "git fetch", Success ""; missingQuery, Failure("no main@origin", 1) ]

    match MainFreshness.reconcileAfterCanary run [ "src/MyLib" ] with
    | Error reason -> test <@ reason.Contains("no main@origin") && reason.Contains("re-run from the new main") @>
    | Ok other -> failwithf "expected a refusal, got %A" other

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``reconcileAfterCanary - when it cannot tell what the new commits touch, it refuses`` (ancestryFails: bool) =
    let broken = Failure("jj exploded", 1)

    let run =
        jjAnswering
            [
                "git fetch", Success ""
                missingQuery, Success landed
                ancestorQuery, (if ancestryFails then broken else Success "")
                pathsQuery, (if ancestryFails then Success "docs/a.md" else broken)
            ]

    match MainFreshness.reconcileAfterCanary run [ "src/MyLib" ] with
    | Error reason ->
        test <@ reason.Contains("jj exploded") @>
        test <@ reason.Contains("deadbeef0001") @>
    | Ok other -> failwithf "expected a refusal, got %A" other

[<Fact>]
let ``reconcileAfterCanary - unrelated commits but a failed move onto main refuses`` () =
    let run =
        jjAnswering
            [
                "git fetch", Success ""
                missingQuery, Success landed
                ancestorQuery, Success ""
                pathsQuery, Success "docs/a.md"
                "new main@origin", Failure("Error: concurrent operation", 1)
            ]

    match MainFreshness.reconcileAfterCanary run [ "src/MyLib" ] with
    | Error reason -> test <@ reason.Contains("concurrent operation") @>
    | Ok other -> failwithf "expected a refusal, got %A" other


[<Fact>]
let ``releaseFootprint - repo-relative package dirs as configured, plus the root changelog of a one-package repo`` () =
    withTempDir (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src", "MyLib")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "MyLib", "MyLib.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let pkg =
            {
                Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = []
            }

        let config =
            {
                Packages = [ pkg ]
                ReservedVersions = Set.empty
                PreBuildCmds = []
                PublishWorkflows = defaultPublishWorkflows
                CiTimeout = None
                RootDir = root
            }

        test <@ releaseFootprint config [ pkg ] = [ "src/MyLib"; "CHANGELOG.md" ] @>)

let private staleMain =
    Failure(
        "Warning: The following references unexpectedly moved on the remote:\n  refs/heads/main (reason: stale info)\nError: Failed to push some bookmarks",
        1
    )

[<Fact>]
let ``main moving between the second fetch and the push tags nothing and abandons the bump commit`` () =
    let output, result, calls, _ = releaseUnder { calm with GitPush = staleMain }

    test <@ result = 1 @>
    test <@ output.Contains("main moved on the remote") @>
    test <@ output.Contains("re-run") @>
    test <@ ran calls "abandon bump0123456789" @>
    test <@ output.Contains("abandoned") @>
    test <@ not (ran calls "tag set") @>

[<Fact>]
let ``a rejected push whose bump commit cannot be abandoned prints the exact jj abandon`` () =
    let output, result, _, _ =
        releaseUnder
            { calm with
                GitPush = staleMain
                Abandon = Failure("Error: some jj failure", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("jj abandon bump0123456789") @>
    test <@ output.Contains("some jj failure") @>

[<Fact>]
let ``a push that fails for another reason keeps the bump commit for a resume and tags nothing`` () =
    let output, result, calls, _ =
        releaseUnder
            { calm with
                GitPush = Failure("Error: failed to authenticate", 1)
            }

    test <@ result = 1 @>
    test <@ output.Contains("failed to authenticate") @>
    test <@ not (output.Contains("main moved on the remote")) @>
    test <@ output.Contains("resume") @>
    test <@ not (ran calls "abandon") @>
    test <@ not (ran calls "tag set") @>

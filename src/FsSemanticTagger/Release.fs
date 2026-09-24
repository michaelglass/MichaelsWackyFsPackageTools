module FsSemanticTagger.Release

open FsSemanticTagger.Version
open FsSemanticTagger.Api
open FsSemanticTagger.Config
open FsSemanticTagger.Shell
open FsSemanticTagger.Vcs

type ReleaseCommand =
    | Auto
    | StartAlpha
    | PromoteToBeta
    | PromoteToRC
    | PromoteToStable

type ReleaseMode =
    | PushTags
    | LocalPublish
    | DryRun

[<NoEquality; NoComparison>]
type ReleaseInput =
    {
        Run: string -> string -> CommandResult
        Config: ToolConfig
        Command: ReleaseCommand
        Mode: ReleaseMode
        /// When non-empty, restrict the run to packages whose `Name` is in this
        /// list (the `--only` filter). Empty = all packages (the default).
        TargetPackages: string list
        /// Fetch a prior release's public API by (packageName, version). It says
        /// whether the API could be READ, never whether the version is PUBLISHED —
        /// that is `CheckFeedPresence`'s question alone. A transient `FetchError`
        /// aborts rather than under-bumps.
        ExtractPreviousApi: string -> string -> PreviousApiResult
        ExtractCurrentApi: string -> ApiSignature list
        /// Recover a prior release's realized CLI grammar by (packageName, version),
        /// and the current build's grammar by DLL path. When BOTH sides yield a
        /// `Grammar`, the grammar diff is folded into the API diff (stronger bump
        /// wins) before `determineBump`, so a `[<Cmd(Name)>]` rename or a flag arity
        /// change bumps even though the assembly signature is unchanged. Either seam
        /// returning `None` leaves the API diff in sole charge of the bump.
        ExtractPreviousGrammar: string -> string -> Grammar option
        ExtractCurrentGrammar: string -> Grammar option
        CiPollIntervalMs: int
        CiMaxAttempts: int
        /// How hard to push each release tag, and how long to keep asking GitHub
        /// whether that tag produced a workflow run. Injected rather than fixed so a
        /// test can bound the poll: the production budget is minutes long, and the
        /// whole point of the tracked issue is that it must be.
        TagPush: Vcs.TagPushPolicy
        /// Ask the feed whether a (packageName, version) is published. The
        /// three-valued answer is what lets an orphan tag be distinguished from an
        /// unreachable feed, so ONE seam serves both the post-push availability
        /// poll and the orphan-tag detection.
        CheckFeedPresence: string -> string -> FeedPresence
        /// Ask whether a (isTool, packageName, version) is RESTORABLE — the gate
        /// between publication waves, whose next wave's nuspec names this exact
        /// version. A separate seam from `CheckFeedPresence` because it is a
        /// stronger question: the index lists a version minutes before a restore
        /// of it succeeds. `isTool` is passed because a tool cannot be probed by
        /// restore at all and has a different strongest answer.
        CheckRestorable: bool -> string -> string -> FeedPresence
        WaitForNuGet: bool
        NuGetPollIntervalMs: int
        NuGetMaxAttempts: int
        /// Opt-in (`--push`): when the release commit isn't on the remote yet,
        /// push it and wait for its CI before proceeding, instead of failing
        /// fast. Default `false` keeps auto-push off, because pushing to a
        /// branch-protected / PR-gated `main` is unsafe to do implicitly.
        Push: bool
        /// `--check`: run only the changelog pre-flight (`runChangelogCheck`) and
        /// exit — no preconditions, no build, no writes, no tags.
        Check: bool
    }

/// Restrict `packages` to those whose `Name` appears in `targetNames`.
///
/// An empty `targetNames` means "no filter" — all packages are returned
/// unchanged (the default behaviour). When names are given, every one must
/// match a package's `Name`; an unknown name is an error that lists the valid
/// names so the caller can fix the typo rather than silently no-op.
let selectPackages (targetNames: string list) (packages: PackageConfig list) : Result<PackageConfig list, string> =
    match targetNames with
    | [] -> Ok packages
    | names ->
        let known = packages |> List.map (fun p -> p.Name) |> Set.ofList

        let unknown =
            names |> List.filter (fun n -> not (known.Contains n)) |> List.distinct

        match unknown with
        | [] -> Ok(packages |> List.filter (fun p -> List.contains p.Name names))
        | bad ->
            let valid = packages |> List.map (fun p -> p.Name) |> String.concat ", "

            Error(
                sprintf
                    "Unknown package(s): %s. Valid package name(s): %s"
                    (String.concat ", " bad)
                    (if valid = "" then "(none)" else valid)
            )

type ReleaseState =
    | FirstRelease
    | HasPreviousRelease of currentVersion: Version

/// Determine new version from current version + API change
let determineBump (current: Version) (change: ApiChange) : Version =
    match current.Stage with
    | PreRelease(RC _) ->
        match change with
        | NoChange -> toStable current
        | _ -> toBeta current
    | PreRelease pre ->
        { current with
            Stage = PreRelease(bumpPreRelease pre) }
    | Stable ->
        match change with
        | Breaking _ ->
            if current.Major >= 1 then
                bumpMajor current
            else
                bumpMinor current
        | Addition _ ->
            if current.Major >= 1 then
                bumpMinor current
            else
                bumpPatch current
        | NoChange -> bumpPatch current

/// Determine version for a specific command (non-Auto)
let forCommand (state: ReleaseState) (cmd: ReleaseCommand) : Result<Version, string> =
    match cmd, state with
    | StartAlpha, FirstRelease -> Ok firstAlpha
    | StartAlpha, HasPreviousRelease v -> Ok(nextAlphaCycle v)
    | PromoteToBeta, HasPreviousRelease v -> Ok(toBeta v)
    | PromoteToRC, HasPreviousRelease v -> Ok(toRC v)
    | PromoteToStable, HasPreviousRelease v -> Ok(toStable v)
    | Auto, _ -> Error "Auto is handled separately"
    | cmd, FirstRelease -> Error $"Cannot {cmd} without a previous release"

let private versionElementRegex =
    System.Text.RegularExpressions.Regex(
        "<Version>([^<]+)</Version>",
        System.Text.RegularExpressions.RegexOptions.Compiled
    )

/// Update <Version> in an fsproj file
let updateFsprojVersion (fsprojPath: string) (version: Version) : unit =
    let content = System.IO.File.ReadAllText(fsprojPath)

    let newContent =
        versionElementRegex.Replace(content, sprintf "<Version>%s</Version>" (format version))

    System.IO.File.WriteAllText(fsprojPath, newContent)

/// Read <Version> from an fsproj file, returning parsed Version if valid
let readFsprojVersion (fsprojPath: string) : Version option =
    let content = System.IO.File.ReadAllText(fsprojPath)
    let m = versionElementRegex.Match(content)

    if m.Success then
        tryParse m.Groups[1].Value |> Result.toOption
    else
        None

/// A wall-clock duration as an operator reads it: `4m12s`, or `0.3s` under a minute.
let internal formatElapsed (elapsed: System.TimeSpan) : string =
    if elapsed.TotalMinutes >= 1.0 then
        sprintf "%dm%02ds" (int elapsed.TotalMinutes) elapsed.Seconds
    else
        sprintf "%.1fs" elapsed.TotalSeconds

/// The wall-clock a poll of `attempts` checks `intervalMs` apart can spend: it sleeps
/// between checks, not after the last one, so N checks are N-1 sleeps.
let internal pollBudget (intervalMs: int) (attempts: int) : System.TimeSpan =
    System.TimeSpan.FromMilliseconds(float (max 0 (attempts - 1)) * float intervalMs)

let internal waitForCi (run: string -> string -> CommandResult) (pollIntervalMs: int) (maxAttempts: int) : CiStatus =
    let rec poll attempt =
        let status = getCiStatus run

        match status with
        | NoRuns when attempt < maxAttempts ->
            printfn "  ...still waiting for CI to start (this is expected — not a hang)"
            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1)
        | InProgress runs when attempt < maxAttempts ->
            let completed =
                runs |> List.filter (fun r -> r.Status = Vcs.Completed) |> List.length

            printfn "  ...CI still running (%d/%d runs complete — expected, not a hang)" completed runs.Length
            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1)
        | InProgress _ ->
            printfn "Timed out waiting for CI after %d attempts" maxAttempts
            status
        | other -> other

    poll 0

/// The NuGet confirmation poll's budget, overridable from the environment with the
/// SAME two variables FsHotWatch's between-stage barrier reads
/// (`FSHW_NUGET_PROBE_ATTEMPTS`, `FSHW_NUGET_PROBE_DELAY_MS`), so one setting tunes
/// both ends of a release.
///
/// 15s x 81 = twenty minutes. The package was measured three times on
/// FsHotWatch (2026-09-15/16) to index 6-15 minutes AFTER the Release run finished,
/// and the old 40 x 15s = 10 min gave up inside that window and printed "Release NOT
/// CONFIRMED" for releases that were fine. FsHotWatch's own barrier was raised to
/// 80 x 15s for the same reason.
let internal defaultNuGetPollIntervalMs = 15000
let internal defaultNuGetMaxAttempts = 81

let nuGetPollFromEnv (getEnv: string -> string option) : int * int =
    Vcs.envIntOrDefault getEnv "FSHW_NUGET_PROBE_DELAY_MS" defaultNuGetPollIntervalMs,
    Vcs.envIntOrDefault getEnv "FSHW_NUGET_PROBE_ATTEMPTS" defaultNuGetMaxAttempts

/// Poll NuGet until every (packageId, version) is restorable, or until
/// `maxAttempts` rounds elapse. `maxAttempts = 1` does exactly one check then gives up.
///
/// RETURNS THE PACKAGES IT COULD NOT CONFIRM, not a bool. Empty
/// means every package is on the feed. Also returns how long it
/// ACTUALLY waited, measured on a stopwatch, and prints that number when it gives up:
/// a poll that stopped after a minute must not say it waited the twenty it was given.
///
/// This used to return `false` on timeout under a doc comment reading "callers
/// MUST NOT fail the release on a false result, the tags are already pushed" —
/// and the caller duly did `|> ignore` and returned 0. That reasoning is half
/// right and the conclusion was wrong: the tags ARE pushed, so a timeout is
/// genuinely not a failed publish, but reporting SUCCESS for a release whose
/// packages nobody has seen is worse than either honest answer.
///
/// The way out is not to pick between them. "Not confirmed" gets its own exit
/// code at the caller, so the release can say what it actually knows: the tags
/// went, the packages have not appeared YET, here is what to check. Returning
/// the names rather than a bool is what makes that message possible.
let internal waitForNuGetTimed
    (checkFeedPresence: string -> string -> FeedPresence)
    (pollIntervalMs: int)
    (maxAttempts: int)
    (packages: (string * string) list)
    : (string * string) list * System.TimeSpan =
    let clock = System.Diagnostics.Stopwatch.StartNew()

    let rec poll attempt pending =
        // Only a definite `OnFeed` clears a package from the poll: an unreachable
        // feed is not evidence of arrival, so keep waiting exactly as for absence.
        let stillPending =
            pending |> List.filter (fun (id, ver) -> checkFeedPresence id ver <> OnFeed)

        if List.isEmpty stillPending then
            []
        elif attempt + 1 >= maxAttempts then
            for id, ver in stillPending do
                printfn
                    "Gave up waiting for %s %s on NuGet after %s (%d checks, %.0fs apart)"
                    id
                    ver
                    (formatElapsed clock.Elapsed)
                    maxAttempts
                    (float pollIntervalMs / 1000.0)

            stillPending
        else
            for id, ver in stillPending do
                printfn "Waiting for %s %s on NuGet... (%s so far)" id ver (formatElapsed clock.Elapsed)

            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1) stillPending

    let unconfirmed = poll 0 packages
    unconfirmed, clock.Elapsed

let internal waitForNuGet
    (checkFeedPresence: string -> string -> FeedPresence)
    (pollIntervalMs: int)
    (maxAttempts: int)
    (packages: (string * string) list)
    : (string * string) list =
    fst (waitForNuGetTimed checkFeedPresence pollIntervalMs maxAttempts packages)

/// Report what the tag push and its confirmation actually established, and pick the
/// exit code that matches.
///
/// Three outcomes, three exit codes — the convention the tracked issue established for the
/// NuGet wait, applied to the same question one stage earlier:
///
/// * `0` — every tag has a workflow run (this function is not called).
/// * `1` — the release DEMONSTRABLY did not happen: a push failed, or a run exists and
///   has already finished without publishing. Both stop an ordered release chain.
/// * `2` — the tags are on the remote and no run has appeared yet. "I stopped waiting"
///   is not "it failed", and the tracked issue is the cost of confusing the two: three
///   healthy releases were reported as broken, and the remedy printed with that verdict
///   would have published each of them a second time.
let internal reportTagConfirmationFailures (failures: TagConfirmationFailure list) : int =
    let pushFailures =
        failures
        |> List.choose (function
            | PushFailed(tag, reason) -> Some(tag, reason)
            | WorkflowRunFailed _
            | WorkflowTriggerMissing _ -> None)

    let failedRuns =
        failures
        |> List.choose (function
            | WorkflowRunFailed(tag, runs) -> Some(tag, runs)
            | PushFailed _
            | WorkflowTriggerMissing _ -> None)

    let missingTriggers =
        failures
        |> List.choose (function
            | WorkflowTriggerMissing(tag, waited, everAnswered) -> Some(tag, waited, everAnswered)
            | PushFailed _
            | WorkflowRunFailed _ -> None)

    if not pushFailures.IsEmpty then
        printfn "Error: %d tag push(es) failed:" pushFailures.Length

        for tag, reason in pushFailures do
            printfn "  %s — %s" tag reason

        printfn ""

        printfn
            "The version-bump commit is already on the remote, so versions in the tree are ahead of the last published release."

        printfn "After fixing authentication or transport, resume by running the same release command again."

        printfn
            "If abandoning the release, reset the bumped versions and changelog to the last published tags before starting another release."

    if not failedRuns.IsEmpty then
        printfn "Error: %d pushed tag(s) have a PUBLISH workflow run that FAILED:" failedRuns.Length

        for tag, runs in failedRuns do
            for runInfo in runs do
                let (Config.PublishWorkflow path) = runInfo.Workflow

                printfn
                    "  %s — publish workflow %s%s finished %A (%s)"
                    tag
                    path
                    (if runInfo.Name = "" then
                         ""
                     else
                         sprintf " (%s)" runInfo.Name)
                    runInfo.Conclusion
                    (if runInfo.Url = "" then "no url reported" else runInfo.Url)

        printfn ""

        printfn
            "This is a real failure, not a slow one: the publishing run exists and it is finished, so nothing published."

        printfn
            "Runs of other workflows on the tag (a docs deploy, say) were not consulted; they cannot refuse a release."

        printfn "Read the log, fix the cause, then resume the SAME run rather than cutting a new tag:"
        printfn "  gh run rerun <id> --failed"

    if not missingTriggers.IsEmpty then
        printfn "Warning: %d pushed tag(s) have no workflow run YET:" missingTriggers.Length

        for tag, waited, everAnswered in missingTriggers do
            printfn
                "  %s — asked for %.0fs; %s"
                tag
                waited.TotalSeconds
                (if everAnswered then
                     "GitHub reported no run"
                 else
                     "`gh` could not be asked at all, so this is NOT evidence of a missing run")

        printfn ""
        printfn "The tags ARE on the remote. This is NOT a failed publish, and it may not be a failure at all:"
        printfn "the run may still be starting. GitHub registers a tag-push run some time after the push, and"
        printfn "this poll can outrun it (raise FSST_RUN_POLL_ATTEMPTS / FSST_RUN_POLL_DELAY_MS to wait longer)."
        printfn ""
        printfn "Do NOT delete and re-push the tag. If the run had merely not registered yet, that publishes twice."
        printfn "Check first, one of:"
        printfn "  gh run list --branch <tag>"
        printfn "  the repository's Actions tab, filtered to the tag"
        printfn ""

        printfn "If, after checking, there is genuinely no run, the tag is an ORPHAN: leave it alone and run the same"

        printfn
            "release command again — the tagger detects an orphan tag whose package never reached the feed and resumes it."

    if pushFailures.IsEmpty && failedRuns.IsEmpty then 2 else 1

/// Explain a NuGet wait that stopped before every package appeared. Exit 2 at the
/// caller, never 1: the tags ARE pushed, so "I stopped waiting" is not "it failed".
let private reportNuGetUnconfirmed (unconfirmed: (string * string) list) (waited: System.TimeSpan) : unit =
    // Exit 2, and NOT 1. Three outcomes, three answers: 0 confirmed
    // on the feed, 1 the publish demonstrably failed (CI red, tags not pushed), 2 the
    // tags went and the packages have not appeared within the window.
    //
    // This used to `|> ignore` the result and return 0, so a release that gave up
    // waiting reported success and the operator learned otherwise by diffing tags
    // against nuget.org by hand.
    //
    // 2 rather than 1 because "I stopped waiting" is not "it failed": the packages
    // may land minutes later, and calling that a failure would train people to re-run
    // a release that already succeeded — a worse habit than the one being fixed.
    //
    // The number printed is the MEASURED wait, and the text names what
    // the evidence of the publish actually is — the tags and their Release runs, not
    // this poll.
    printfn ""

    printfn
        "Release NOT CONFIRMED: %d package(s) had not appeared on NuGet when this poll stopped waiting after %s."
        (List.length unconfirmed)
        (formatElapsed waited)

    for id, ver in unconfirmed do
        printfn "  unconfirmed: %s %s" id ver

    printfn ""

    printfn "This is NOT a failed publish. The tags ARE pushed and each has a Release run; those are the"

    printfn "evidence of the publish. NuGet's index lags the Release run by 6-15 minutes (measured), and"

    printfn "this poll only stopped watching for it."
    printfn ""
    printfn "Check https://www.nuget.org/packages/<id>/<version> for each. If the Release run itself"
    printfn "failed rather than lagged, resume it with:  gh run rerun <id> --failed"
    printfn ""
    printfn "Re-running the same release command RESUMES this release — it detects the pushed tags and"
    printfn "does not publish a second time. To wait longer next time, set FSHW_NUGET_PROBE_ATTEMPTS"
    printfn "and/or FSHW_NUGET_PROBE_DELAY_MS."

/// Push the release's tags wave by wave (`ReleaseOrder.waves`), so a package is never
/// made visible before a separately released dependency that ships in the same
/// release.
///
/// Every tag of a wave is pushed and its publish run confirmed, as for a single-wave
/// release. When a later wave is waiting on this one, the exact versions of this wave
/// must then be RESTORABLE before any later tag is pushed — `CheckRestorable`, not
/// `CheckFeedPresence`, because the next wave's nuspec names these versions and the
/// index lists a version minutes before it restores. That wait is not optional:
/// `--skip-nuget-wait` skips only the final confirmation, because skipping a gate
/// would let a dependent run race its dependency. When the gate gives up, the
/// dependents' tags stay unpushed and the release exits 2; re-running the same
/// command resumes it.
///
/// NuGet is asked about each package ONCE per wave. The final wave's confirmation
/// uses the index (`CheckFeedPresence`) and is what `--skip-nuget-wait` drops, so a
/// consumer whose release runs its own restorability barrier after this command
/// (FsHotWatch's `scripts/wait-for-nuget.fsx`) asks the stronger question exactly
/// once for that wave too, instead of this poll asking the weaker one first.
let private pushTagsInWaves (input: ReleaseInput) (waves: (PackageConfig * Version) list list) : int =
    let rec publish (remaining: (PackageConfig * Version) list list) =
        match remaining with
        | [] -> 0
        | wave :: later ->
            let tags = wave |> List.map (fun (pkg, version) -> toTag pkg.TagPrefix version)
            let pkgVersions = wave |> List.map (fun (pkg, version) -> pkg.Name, format version)

            // A tag can land on the remote and trigger no workflow at all (a batch push
            // does exactly that), so confirm a run exists rather than claiming a release
            // is happening.
            let unconfirmedTags =
                pushTagsAndConfirmDetailed input.Run input.Config.PublishWorkflows input.TagPush tags

            if not (List.isEmpty unconfirmedTags) then
                if not (List.isEmpty later) then
                    printfn
                        "Not pushing the tags of the packages that depend on them: %s"
                        (later
                         |> List.concat
                         |> List.map (fun (pkg, version) -> toTag pkg.TagPrefix version)
                         |> String.concat ", ")

                reportTagConfirmationFailures unconfirmedTags
            else
                printfn "Tags pushed, and a workflow run exists for each. GitHub Actions will handle the release."

                let gatesLater = not (List.isEmpty later)

                if gatesLater || input.WaitForNuGet then
                    let tools =
                        wave
                        |> List.filter (fun (pkg, _) -> isPackAsTool (System.IO.File.ReadAllText pkg.Fsproj))
                        |> List.map (fun (pkg, _) -> pkg.Name)
                        |> Set.ofList

                    let check =
                        if gatesLater then
                            fun id ver -> input.CheckRestorable (Set.contains id tools) id ver
                        else
                            input.CheckFeedPresence

                    if gatesLater then
                        printfn
                            "Waiting for %s to be restorable from NuGet before pushing the packages that depend on them — up to %s (%d checks, %.0fs apart)..."
                            (pkgVersions |> List.map (fun (id, ver) -> id + " " + ver) |> String.concat ", ")
                            (formatElapsed (pollBudget input.NuGetPollIntervalMs input.NuGetMaxAttempts))
                            input.NuGetMaxAttempts
                            (float input.NuGetPollIntervalMs / 1000.0)
                    else
                        printfn
                            "Waiting for NuGet to index the published package(s) — up to %s (%d checks, %.0fs apart); the index typically lags the Release run by 6-15 min..."
                            (formatElapsed (pollBudget input.NuGetPollIntervalMs input.NuGetMaxAttempts))
                            input.NuGetMaxAttempts
                            (float input.NuGetPollIntervalMs / 1000.0)

                    let unconfirmed, waited =
                        waitForNuGetTimed check input.NuGetPollIntervalMs input.NuGetMaxAttempts pkgVersions

                    if List.isEmpty unconfirmed then
                        publish later
                    else
                        reportNuGetUnconfirmed unconfirmed waited

                        if gatesLater then
                            printfn ""

                            printfn
                                "The packages that depend on them were NOT published, so none can reach the feed ahead of its dependency. Their tags exist locally but are not pushed:"

                            for pkg, version in List.concat later do
                                printfn "  held back: %s" (toTag pkg.TagPrefix version)

                            printfn
                                "Re-running the same release command pushes them once the dependencies are on NuGet."

                        2
                else
                    0

    publish waves

let private waitForCiAndPushTags
    (input: ReleaseInput)
    (graph: ReleaseOrder.ReleaseGraph)
    (bumps: (PackageConfig * Version) list)
    : int =
    printfn "Waiting for CI on the version-bump commit to pass before pushing the tag (expected, ~1-2 min)..."

    match waitForCi input.Run input.CiPollIntervalMs input.CiMaxAttempts with
    | Passed -> pushTagsInWaves input (ReleaseOrder.waves graph (fun (pkg: PackageConfig, _) -> pkg.Name) bumps)
    | Failed runs ->
        printfn "Error: CI failed on version bump commit. Not pushing tags."

        for r in runs do
            printfn "  FAILED: %s — %s" r.Name r.Url

        1
    | InProgress _ ->
        printfn "Error: CI still running after timeout. Not pushing tags."
        printfn "Run the release command again to resume."
        1
    | _ ->
        printfn "Error: could not determine CI status. Not pushing tags."
        printfn "Run the release command again to resume."
        1

let private packLocally (run: string -> string -> CommandResult) (bumps: (PackageConfig * Version) list) : int =
    for (pkg, _version) in bumps do
        // -p:ReleaseBuild=true: local-publish is the RELEASE pipeline running on a
        // dev machine — it owns the clean semver it just computed. Without the flag
        // the RefStamp guard refuses to emit a release-shaped version from a local
        // pack.
        runOrFail run "dotnet" (sprintf "pack %s -c Release -p:ReleaseBuild=true -o artifacts/" pkg.Fsproj)
        |> ignore

        printfn "Packed: %s" pkg.Name

    0

/// What caused a bump, so downstream changelog handling can adapt.
/// `OwnChange` is the normal case (the package's own source changed, strict
/// CHANGELOG validation applies). `DependencyChange` is a "rebundle" bump
/// triggered solely because a transitive `<ProjectReference>` of a bundling
/// package (e.g. a `PackAsTool` CLI that physically ships the referenced DLLs)
/// changed; its real change is documented in the dependency's changelog, so the
/// package's own `## Unreleased` may legitimately be missing/empty.
type BumpTrigger =
    | OwnChange
    | DependencyChange

type BumpDecision =
    | NeedsBump of PackageConfig * Version * BumpTrigger
    | AlreadyBumped of PackageConfig * Version
    /// Auto mode couldn't read the previous release's API, so the bump can't be
    /// computed. We refuse to guess (a breaking change must not ship as a patch).
    | CannotDetermine of PackageConfig * reason: string

/// The bundled-dependency directories whose changes count toward `pkg`: its
/// transitive `<ProjectReference>` closure, pruned at every separately-released
/// package boundary (those are NuGet `<dependency>` boundaries, not bundled).
/// Repo-root-relative, forward slashes. Shared by change-detection and changelog
/// derivation so both attribute commits to a package identically.
let internal packageDepDirs (config: ToolConfig) (pkg: PackageConfig) : string list =
    let separatelyReleased =
        config.Packages |> List.map (fun p -> p.Fsproj.Replace('\\', '/')) |> Set.ofList

    let isSeparatelyReleased (fsprojRel: string) =
        separatelyReleased.Contains(fsprojRel.Replace('\\', '/'))

    transitiveBundledRefDirs config.RootDir pkg.Fsproj isSeparatelyReleased

/// Every directory whose changes are attributed to `pkg`: its own source dir
/// plus its bundled-dependency dirs. The change/description closure used when
/// deriving the `## Unreleased` section from commits since the last tag.
let internal packageChangeDirs (config: ToolConfig) (pkg: PackageConfig) : string list =
    System.IO.Path.GetDirectoryName(pkg.Fsproj) :: packageDepDirs config pkg

/// Collect (packageName, changelogPath) pairs for a package.
/// Single-package repos use repo-root CHANGELOG.md; multi-package repos use per-fsproj-dir.
let internal changelogPathsFor (config: ToolConfig) (pkg: PackageConfig) : (string * string) list =
    if config.Packages.Length = 1 then
        [ pkg.Name, System.IO.Path.Combine(config.RootDir, "CHANGELOG.md") ]
    else
        pkg.Fsproj :: pkg.FsProjsSharingSameTag
        |> List.map System.IO.Path.GetDirectoryName
        |> List.distinct
        |> List.map (fun dir -> pkg.Name, System.IO.Path.Combine(dir, "CHANGELOG.md"))

/// The fsprojs whose dependency changes `changelogPath` records: every fsproj of
/// the package for a single-package repo's root changelog, otherwise the ones
/// beside it (the same attribution `changelogPathsFor` uses to find it).
let internal fsprojsForChangelog (config: ToolConfig) (pkg: PackageConfig) (changelogPath: string) : string list =
    let fsprojs = pkg.Fsproj :: pkg.FsProjsSharingSameTag

    if config.Packages.Length = 1 then
        fsprojs
    else
        let dir = System.IO.Path.GetDirectoryName changelogPath
        fsprojs |> List.filter (fun f -> System.IO.Path.GetDirectoryName f = dir)

/// Consumer-visible `<PackageReference>` changes in `fsprojs` between `tag` and
/// the working copy. An fsproj with no readable baseline at the tag (new since
/// the release) or none on disk derives nothing: without both sides there is no
/// change to state, and claiming every reference as "added" would be false.
let internal dependencyChangesSinceTag
    (run: string -> string -> CommandResult)
    (config: ToolConfig)
    (tag: string)
    (fsprojs: string list)
    : Changelog.PackageRefChange list =
    fsprojs
    |> List.collect (fun fsproj ->
        let onDisk = System.IO.Path.Combine(config.RootDir, fsproj)

        let current =
            if System.IO.File.Exists onDisk then
                Changelog.packageReferences (System.IO.File.ReadAllText onDisk)
            else
                None

        match fileAtRevision run tag fsproj |> Option.bind Changelog.packageReferences, current with
        | Some before, Some after -> Changelog.diffPackageReferences before after
        | _ -> [])
    |> List.distinct

/// The promotion plan for each of `pkg`'s changelogs, as (package name, path,
/// plan). THE single computation behind both `release --check` and the release's
/// promotion, so the check can only report what promotion will write. With no
/// prior tag there is no "since" range: nothing is derived, and the section must
/// be authored.
let internal promotionPlans
    (input: ReleaseInput)
    (pkg: PackageConfig)
    (latestTag: string option)
    : (string * string * Result<Changelog.PromotionPlan, Changelog.ChangelogError>) list =
    let descriptions, dependencyChangesFor =
        match latestTag with
        | Some tag ->
            descriptionsSinceTag input.Run tag (packageChangeDirs input.Config pkg),
            dependencyChangesSinceTag input.Run input.Config tag
        | None -> [], (fun _ -> [])

    changelogPathsFor input.Config pkg
    |> List.map (fun (pkgName, path) ->
        let changes = dependencyChangesFor (fsprojsForChangelog input.Config pkg path)
        pkgName, path, Changelog.planPromotion path descriptions changes)

/// The changelogs whose callout order is checked: every selected package's
/// changelog, plus the repo-root `CHANGELOG.md` when one exists. The root file
/// is the reader-facing aggregate — it is where a "read this first" callout
/// actually lives, and where a merge buries it — even though a multi-package
/// repo's tool-managed promotion never touches it. In a single-package repo
/// `changelogPathsFor` already returns that same path, so the dedupe keeps one
/// entry under the package's own name.
let internal calloutCheckPaths (config: ToolConfig) (packages: PackageConfig list) : (string * string) list =
    let rootPath = System.IO.Path.Combine(config.RootDir, "CHANGELOG.md")

    (packages |> List.collect (changelogPathsFor config))
    @ [ "repo root", rootPath ]
    |> List.distinctBy snd

/// Changelogs whose `## Unreleased` callout is no longer the first thing in the
/// section. Unlike an empty section this is NEVER suppressed by "derivable from
/// commits": deriving bullets cannot move a callout back to the top, and a
/// release would freeze the wrong order into a published version section.
let internal calloutOrderProblems
    (config: ToolConfig)
    (packages: PackageConfig list)
    : (string * Changelog.ChangelogError) list =
    calloutCheckPaths config packages
    |> List.choose (fun (name, path) ->
        match Changelog.validateCalloutOrder path with
        | Ok() -> None
        | Error err -> Some(name, err))

/// The actionable error printed when the release commit isn't on the remote, so
/// no CI run could ever exist for it. Names the fix and points at `--push`, and
/// never mislabels a *missing* run as a *failed* one. Kept as a value so the
/// wording is pinned by one test.
let internal notPushedMessage: string =
    "Error: the release commit isn't on the remote, so no CI run exists for it (it \
     hasn't been pushed yet).\n\
     loosen-from-ci needs the commit's CI coverage artifact (to reconcile the \
     Linux-CI vs local coverage floors), so the commit must be pushed and its CI \
     must finish first.\n\
     Push the branch and wait for CI, then re-run the release — or pass --push to \
     push and wait for CI automatically."

/// Wait for the release commit's CI to complete and translate the terminal
/// status into a release verdict. Reused for both "already pushed" and "just
/// pushed via --push" — the single place that decides go / no-go. `NoRuns` is
/// reported as itself: a run that never registered is not a run that failed, and
/// neither is the unpushed precondition.
let private waitForReleaseCi (input: ReleaseInput) : Result<unit, int> =
    printfn "Waiting for CI on the release commit to pass before releasing (expected, ~1-2 min)..."

    match waitForCi input.Run input.CiPollIntervalMs input.CiMaxAttempts with
    | Passed -> Ok()
    | Failed runs ->
        printfn "Error: CI failed for the release commit. Fix CI before releasing."

        for r in runs do
            printfn "  FAILED: %s — %s" r.Name r.Url

        Error 1
    | NoRuns ->
        printfn "Error: no CI run registered for the release commit before the timeout. Re-run the release."
        Error 1
    | InProgress _ ->
        printfn "Error: CI still running after timeout. Re-run the release once it finishes."
        Error 1
    | Unknown ->
        printfn "Error: could not determine the release commit's CI status (is `gh` installed and authenticated?)."
        Error 1

/// FAIL-FAST CI precondition, run *before* the expensive coverage reconciliation.
/// "No CI run" has two causes needing opposite handling: a commit not on the
/// remote can never have a run (fail fast, or push it with `--push`), whereas a
/// pushed commit whose run hasn't registered yet must be *waited* for — which is
/// also the right-after-push race.
let private confirmReleaseCommitCiGreen (input: ReleaseInput) : Result<unit, int> =
    match releaseCommitSha input.Run with
    | None ->
        printfn "Error: could not determine the release commit (no VCS sha). Cannot verify CI before releasing."
        Error 1
    | Some sha ->
        if isCommitPushed input.Run sha then
            waitForReleaseCi input
        elif input.Push then
            printfn "Release commit isn't pushed yet; --push given, pushing and waiting for CI..."
            pushMain input.Run
            waitForReleaseCi input
        else
            printfn "%s" notPushedMessage
            Error 1

/// Reconcile the local coverage floors against the green CI run's coverage
/// artifact (`coverageratchet loosen-from-ci`). Runs only *after* the CI
/// precondition has confirmed the commit is pushed and CI is green, so it can
/// never hit loosen-from-ci's "no CI runs" path — it does the coverage
/// reconciliation it is actually for. A no-op when coverageratchet isn't a local
/// tool.
let private reconcileCoverageFromCi (input: ReleaseInput) : Result<unit, int> =
    if hasCoverageRatchet input.Run then
        printfn "Reconciling coverage floors from the green CI run (coverageratchet loosen-from-ci)..."

        match input.Run "dotnet" "tool run coverageratchet loosen-from-ci" with
        | Success _ -> Ok()
        | Failure(msg, _) ->
            printfn "Error: coverageratchet loosen-from-ci failed"

            if msg <> "" then
                printfn "  %s" msg

            Error 1
    else
        Ok()

let private preReleaseChecks (input: ReleaseInput) : Result<unit, int> =
    match input.Mode with
    | DryRun -> Ok()
    | PushTags
    | LocalPublish ->
        if hasUncommittedChanges input.Run then
            printfn
                "Error: uncommitted changes detected. Commit (or, in jj, describe `@`) the working copy before releasing."

            Error 1
        else
            // Precondition FIRST: the release commit is pushed and its CI green,
            // before any expensive coverage reconciliation.
            confirmReleaseCommitCiGreen input
            |> Result.bind (fun () -> reconcileCoverageFromCi input)

let private runPreBuild (input: ReleaseInput) : unit =
    for preBuildCmd in input.Config.PreBuildCmds do
        printfn "Running: %s" preBuildCmd
        let parts = preBuildCmd.Split(' ', 2)
        let cmd = parts[0]
        let args = if parts.Length > 1 then parts[1] else ""
        runOrFail input.Run cmd args |> ignore

    printfn "Building in Release mode..."
    runOrFail input.Run "dotnet" "build -c Release" |> ignore

/// The version the working tree declares it is, read tolerantly: a missing (or
/// unversioned) fsproj yields `None` rather than throwing, so such a package
/// defers to the normal path instead of crashing the whole release run.
/// `readFsprojVersion` alone throws on a missing file, so every caller that may
/// run before the fsproj is known to exist must go through this.
let private declaredFsprojVersion (pkg: PackageConfig) : Version option =
    if System.IO.File.Exists pkg.Fsproj then
        readFsprojVersion pkg.Fsproj
    else
        None

/// Detect a release that was started but never finished, so the next run resumes
/// it instead of starting a new one.
///
/// A release is finished only once its tag exists AND its package is actually on
/// the feed — a tag is a promise to publish, not the publication. This function
/// owns the tag-shaped half: the mid-release failure between the version-bump
/// commit and the CI-poll/tag step. It is decided purely from the *desired end
/// state*, never from work-remaining (a half-rolled changelog, the
/// latest-tag-to-HEAD diff, an API comparison): the fsproj `<Version>` is the
/// intended release version, so a release is bumped-but-untagged exactly when it
/// is strictly ahead of the latest tag AND no tag exists at
/// `<prefix><fsprojVersion>`.
///
/// The feed-shaped half — an ORPHAN tag, one whose package never landed — is
/// deliberately NOT decided here: it turns on whether there are source changes
/// since that tag, which this function cannot see. It lives at `decideBump`'s
/// no-changes arm, which holds the change flags.
///
/// When this holds we return the fsproj version so the caller resumes from the
/// CI-poll + tag step (idempotent finish) instead of recomputing a fresh bump,
/// re-rolling the changelog, or aborting on an unreadable previous API.
let private inProgressResumeVersion (input: ReleaseInput) (state: ReleaseState) (pkg: PackageConfig) : Version option =
    match state with
    | FirstRelease -> None
    | HasPreviousRelease latestTagVersion ->
        match declaredFsprojVersion pkg with
        | Some v when
            sortKey v > sortKey latestTagVersion
            && not (tagExists input.Run (toTag pkg.TagPrefix v))
            ->
            Some v
        | _ -> None

/// One prior release, as a candidate diff baseline. Readability and publication
/// are separate facts with separate authorities — the API extractor for the
/// first, the feed (`CheckFeedPresence`) for the second — and this type keeps the
/// three that matter apart instead of collapsing two of them into "no API".
type private PriorRelease =
    /// Published, and its public API was read.
    | PublishedWithApi of ApiSignature list
    /// Not shown to be absent from the feed, but its public API could not be read
    /// (no assembly in the package, an assembly that will not load, or a restore
    /// that could not find it). Carries why. It is still the baseline this release
    /// follows, so it must never be skipped in favour of an older one.
    | PublishedUnreadable of reason: string
    /// The feed definitively does not have this version: an orphan tag, whose
    /// release publish never landed.
    | NotPublished
    /// A transient fetch fault: the truth is unknown.
    | Unknown of fetchError: string

/// Classify one prior release. Absence is established ONLY by the feed — exactly
/// as `isOrphanRelease` does — and never inferred from a failure to read the API:
/// `MichaelGlass.FSharp.Analyzers` alpha.1–4 were all on the feed, yet an
/// unresolvable `FSharp.Analyzers.SDK` made each read fail and each was reported
/// as an orphan and skipped. `FeedUnknown` counts as published,
/// for the same asymmetry `isOrphanRelease` documents.
let private classifyPriorRelease (input: ReleaseInput) (pkg: PackageConfig) (version: Version) : PriorRelease =
    let unreadableUnlessAbsent reason =
        match input.CheckFeedPresence pkg.Name (format version) with
        | NotOnFeed -> NotPublished
        | OnFeed
        | FeedUnknown _ -> PublishedUnreadable reason

    match input.ExtractPreviousApi pkg.Name (format version) with
    | Found api -> PublishedWithApi api
    | FetchError msg -> Unknown msg
    | Unreadable reason -> unreadableUnlessAbsent reason
    | NotRestorable reason -> unreadableUnlessAbsent (sprintf "restore could not find it: %s" reason)

/// The baseline API to diff the current build against, having walked the release
/// tags newest-first looking for one whose package is actually published.
type private BaselineApi =
    /// Found a published prior release to diff against: the version it resolved to
    /// (so the same release's grammar can be fetched at the fold point) and its API
    /// surface.
    | BaselineFound of version: Version * api: ApiSignature list
    /// The newest published prior release — the baseline this release actually
    /// follows — is published but its API could not be read. Carries its tag and
    /// why. The walk stops here: diffing against an older release instead would
    /// compare against the wrong API surface and could under-bump.
    | BaselineUnreadable of tag: string * reason: string
    /// Every prior tag's package is genuinely absent on the feed (all orphan
    /// tags). There is no published prior to diff against, so the caller falls
    /// back to first-release handling rather than guessing or aborting.
    | NoPublishedPrior
    /// A transient/network/auth fetch error — the truth is unknown, so the caller
    /// MUST abort rather than risk under-bumping a breaking change. Carries the
    /// underlying restore-failure message so the abort can surface *why*.
    | BaselineFetchError of fetchError: string

/// Resolve the API surface to diff against in Auto mode. Walks `sortedTags`
/// (newest-first) and stops at the first release that is not an orphan: its API if
/// readable, `BaselineUnreadable` if not. Only a release the FEED says is absent is
/// skipped (with a warning naming its tag). Any `FetchError` aborts immediately (a
/// genuine outage must never be silently skipped). Exhausting the list with only
/// orphans yields `NoPublishedPrior`.
let private resolveBaselineApi
    (input: ReleaseInput)
    (pkg: PackageConfig)
    (sortedTags: (string * Version) list)
    : BaselineApi =
    sortedTags
    |> List.tryPick (fun (tag, version) ->
        match classifyPriorRelease input pkg version with
        | PublishedWithApi api -> Some(BaselineFound(version, api))
        | PublishedUnreadable reason -> Some(BaselineUnreadable(tag, reason))
        | Unknown msg -> Some(BaselineFetchError msg)
        | NotPublished ->
            printfn
                "Warning: %s package for tag %s is not on the feed (orphan tag — its release publish never landed on NuGet). Skipping it and diffing against the previous published release."
                pkg.Name
                tag

            None)
    |> Option.defaultValue NoPublishedPrior

/// Does the version computed from `current` depend on the API diff at all? For an
/// alpha or beta it does not — the pre-release counter advances whatever the diff
/// says — so an unreadable baseline cannot change the answer there, and refusing
/// would block the release for nothing. Asked of `determineBump` itself rather than
/// by matching on stages, so it stays true if the bump rules change.
let private bumpDependsOnApiDiff (current: Version) : bool =
    let probe = ApiSignature ""

    [ Breaking(probe, []); Addition(probe, []) ]
    |> List.exists (fun change -> determineBump current change <> determineBump current NoChange)

/// Is the release tagged at `version` an ORPHAN — tagged, but its package never
/// landed on the feed?
///
/// This asks the FEED (`CheckFeedPresence`), not the API extractor. The question
/// is presence, not shape, and the API extractor is a proxy with failure modes of
/// its own:
///
///   * A `PackAsTool` package can never be API-probed — a `PackageReference` to a
///     tool package fails NU1212, which classifies as `FetchError`. The API seam
///     can therefore NEVER say "absent" for a tool, and every dotnet tool
///     (including this one) would stay wedged.
///   * The extractor cannot read the API of a package that IS published but ships
///     no DLL (an MSBuild-only package) or whose DLL will not load (an analyzer
///     whose SDK does not resolve). Driving a REPUBLISH off that signal re-releases
///     a perfectly published version on every such package.
///
/// The feed check has neither failure mode: it reads the published version list
/// directly and is blind to package internals.
///
/// Only a definite `NotOnFeed` answers true. `FeedUnknown` is folded in with
/// `OnFeed` because the two wrong guesses are not symmetric: guessing "absent"
/// during an outage re-publishes an already-published version on every run, while
/// guessing "published" only defers finishing to the next run that can reach the
/// feed.
let private isOrphanRelease (input: ReleaseInput) (pkg: PackageConfig) (version: Version) : bool =
    match input.CheckFeedPresence pkg.Name (format version) with
    | NotOnFeed -> true
    | OnFeed
    | FeedUnknown _ -> false

let private decideBump (input: ReleaseInput) (pkg: PackageConfig) : BumpDecision option =
    let sortedTags = getSortedTags input.Run pkg.TagPrefix

    let state =
        match sortedTags with
        | (_, version) :: _ -> HasPreviousRelease version
        | [] -> FirstRelease

    let ownSrcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)

    // A referenced project contributes to this package's change-detection closure
    // only if its DLL actually ships inside the package — see `packageDepDirs`.
    let depDirs = packageDepDirs input.Config pkg

    match inProgressResumeVersion input state pkg with
    | Some resumeVersion ->
        // Bumped-but-untagged: finish the existing release rather than starting a
        // new one. Skips the "no changes since tag" no-op, the Auto API recompute,
        // and the changelog re-roll — all of which assume work still to be done.
        AlreadyBumped(pkg, resumeVersion) |> Some
    | None ->
        let toDecision (trigger: BumpTrigger) (newVersion: Version) =
            if readFsprojVersion pkg.Fsproj = Some newVersion then
                AlreadyBumped(pkg, newVersion)
            else
                NeedsBump(pkg, newVersion, trigger)

        // Apply an explicit (non-Auto) command's stage transition. Explicit
        // commands bypass API diffing entirely, so the resulting version comes
        // straight from `forCommand`; the reserved-version skip and the
        // forCommand error are handled once here for every explicit path
        // (own-changed, dep-only, and first-release). `trigger` records whether
        // this was an own-source bump or a dependency-only rebundle.
        let explicitBump (trigger: BumpTrigger) =
            match forCommand state input.Command with
            | Ok v ->
                if input.Config.ReservedVersions.Contains(format v) then
                    printfn "Warning: version %s is reserved, skipping" (format v)
                    None
                else
                    Some(toDecision trigger v)
            | Error msg ->
                printfn "%s for %s" msg pkg.Name
                None

        // Apply the reserved-version patch-skip: if a computed bump lands on a
        // reserved version, step past it with a patch bump. Shared by every Auto
        // bump path (dependency rebundle, all-orphan fallback, own-change diff).
        let skipReserved (v: Version) =
            if input.Config.ReservedVersions.Contains(format v) then
                bumpPatch v
            else
                v

        // A dependency-triggered "rebundle" bump: the package's own source is
        // unchanged but a bundled `<ProjectReference>` changed. A bundled tool/exe
        // has no meaningful public API to diff (and ExtractPreviousApi would fail
        // -> CannotDetermine), so treat it as a NoChange-style bump, honouring the
        // existing reserved-version patch-skip.
        let depBumpAuto (currentVersion: Version) (tag: string) =
            let newVersion = skipReserved (determineBump currentVersion NoChange)
            printfn "Bumping %s: bundled dependency changed since %s (rebundle)" pkg.Name tag
            Some(toDecision DependencyChange newVersion)

        match state with
        | HasPreviousRelease currentVersion ->
            let tag = toTag pkg.TagPrefix currentVersion
            let ownChanged = hasChangesSinceTag input.Run tag ownSrcDir
            let depChanged = depDirs |> List.exists (hasChangesSinceTag input.Run tag)

            // An own-change Auto bump from the computed `change`, floored by what the
            // changelog(s) behind this tag declare (`DeclaredBump`): the API diff cannot
            // see a changed `[<Literal>]`, but an author who wrote `feat!:` has said it
            // breaks. Every disagreement is printed; no markers leaves `change` as is.
            let ownChangeBump (change: ApiChange) =
                let declared =
                    changelogPathsFor input.Config pkg
                    |> List.choose (fun (_, path) ->
                        Changelog.promotedEntryLines path (fun () ->
                            descriptionsSinceTag input.Run tag (packageChangeDirs input.Config pkg))
                        |> DeclaredBump.declare path)
                    |> DeclaredBump.strongest

                let floored, report = DeclaredBump.floor change declared
                report |> Option.iter (printfn "%s: %s" pkg.Name)
                Some(toDecision OwnChange (skipReserved (determineBump currentVersion floored)))

            match ownChanged, depChanged with
            | false, false ->
                // Nothing left to BUILD — but that is a FINISHED release only if the
                // newest tag's package actually reached the feed. An orphan tag
                // leaves the release unfinished with an empty diff, and the plain
                // skip below wedges it permanently: no change can ever appear "since"
                // a tag that already sits at HEAD.
                //
                // Resume it instead, at THAT SAME version — the tree is still exactly
                // what the version was cut from, so `resumeAlreadyBumped` just pushes
                // the existing tag and lets CI publish; nothing is re-bumped and the
                // changelog is not re-rolled.
                //
                // Guarded on the fsproj declaring that same version, because the
                // resume publishes whatever `<Version>` the tree carries: a tree that
                // says something else would ship the wrong version.
                if
                    declaredFsprojVersion pkg = Some currentVersion
                    && isOrphanRelease input pkg currentVersion
                then
                    printfn
                        "Resuming %s: tag %s exists but its package never landed on the feed (orphan tag). Finishing that release rather than skipping."
                        pkg.Name
                        tag

                    Some(AlreadyBumped(pkg, currentVersion))
                else
                    printfn "Skipping %s: no changes since %s" pkg.Name tag
                    None
            | false, true ->
                match input.Command with
                | Auto -> depBumpAuto currentVersion tag
                | _ ->
                    printfn "Bumping %s: bundled dependency changed since %s (rebundle)" pkg.Name tag
                    explicitBump DependencyChange
            | true, _ ->
                match input.Command, isPackAsTool (System.IO.File.ReadAllText pkg.Fsproj) with
                | Auto, true ->
                    // A PackAsTool package has no library API surface to diff: a
                    // PackageReference to a tool package fails NU1212, which would land
                    // every own-source change (a CHANGELOG edit included) in
                    // CannotDetermine. So the API probe stays skipped here.
                    //
                    // A tool still has a CLI CONTRACT, and skipping the API probe must
                    // not also skip the grammar diff, or every dotnet tool we ship
                    // releases a breaking CLI change as a patch. The two are
                    // independent: the grammar extractor reads a prior release straight
                    // out of the NuGet cache and constructs no PackageReference probe,
                    // so it cannot raise NU1212. Walk the tags newest-first for the
                    // first whose grammar is readable, mirroring `resolveBaselineApi`.
                    let previousGrammar =
                        sortedTags
                        |> List.tryPick (fun (_, version) -> input.ExtractPreviousGrammar pkg.Name (format version))

                    match previousGrammar, input.ExtractCurrentGrammar pkg.DllPath with
                    | Some previousGrammar, Some currentGrammar ->
                        // Folded against a `NoChange` API baseline — a tool has no library
                        // API, so the grammar alone decides. Reusing `foldIntoApi` keeps
                        // one translation from GrammarChange to ApiChange, not two.
                        let change =
                            Grammar.foldIntoApi NoChange (Grammar.compare previousGrammar currentGrammar)

                        printfn
                            "Bumping %s: own change to a PackAsTool package — CLI grammar diffed since %s"
                            pkg.Name
                            tag

                        ownChangeBump change
                    | None, Some _ ->
                        // FAIL CLOSED. This package HAS a CLI grammar, but the previous
                        // release's could not be read — the extractor is cache-only and
                        // this path skips the API download that would have populated the
                        // cache, so a cold cache lands here. Bumping NoChange would
                        // release a possibly-breaking CLI change as a patch, so refuse to
                        // guess, mirroring the non-tool arm's `BaselineFetchError ->
                        // CannotDetermine`.
                        Some(
                            CannotDetermine(
                                pkg,
                                sprintf
                                    "could not read the CLI grammar of the previous release %s: the package is not in the local NuGet cache, and a PackAsTool package is deliberately not API-probed (NU1212), so nothing populates it. Refusing to guess the version bump — a breaking CLI change would otherwise ship as a patch. Fix: restore/populate the cache for %s %s (e.g. `dotnet tool install --tool-path <tmp> %s --version %s`), then re-run; or use an explicit alpha/beta/rc/stable command."
                                    tag
                                    pkg.Name
                                    tag
                                    pkg.Name
                                    tag
                            )
                        )
                    | _, None ->
                        // No current grammar: this package is not a CommandTree consumer
                        // and has no CLI contract to protect. Deliberately NOT failing
                        // closed — that would block every non-CLI PackAsTool release on a
                        // guard that does not apply to it.
                        printfn
                            "Bumping %s: own change to a PackAsTool package (not a CommandTree CLI — no grammar to diff) since %s"
                            pkg.Name
                            tag

                        ownChangeBump NoChange
                | Auto, false ->
                    // Diff against the most recent *published* prior release,
                    // walking back past any orphan tags (whose package never landed
                    // on NuGet) so a missed publish doesn't block the next release.
                    match resolveBaselineApi input pkg sortedTags with
                    | BaselineFetchError msg ->
                        // The feed was unreachable, so the previous API is unknown.
                        // Treating that as "no change" would ship a breaking release as a
                        // patch — refuse to guess, and surface the restore error.
                        Some(
                            CannotDetermine(
                                pkg,
                                sprintf
                                    "could not read the public API of the previous release %s (package not in the NuGet cache and download failed — check network/feed access). Refusing to guess the version bump; re-run once the package is reachable, or use an explicit alpha/beta/rc/stable command. (fetch error: %s)"
                                    tag
                                    msg
                            )
                        )
                    | BaselineUnreadable(unreadableTag, reason) when not (bumpDependsOnApiDiff currentVersion) ->
                        printfn
                            "Bumping %s: the public API of %s could not be read (%s), but the bump from %s does not depend on the API diff"
                            pkg.Name
                            unreadableTag
                            reason
                            (format currentVersion)

                        ownChangeBump NoChange
                    | BaselineUnreadable(unreadableTag, reason) ->
                        // FAIL CLOSED. The release this one follows IS published, so it
                        // is the only correct baseline; walking back to an older tag
                        // would diff against the wrong API surface and could ship a
                        // breaking change as a patch. Fixing the load context is not a
                        // safe guess (an analyzer package does not declare the SDK it
                        // builds against), so refuse and say exactly what failed.
                        Some(
                            CannotDetermine(
                                pkg,
                                sprintf
                                    "could not read the public API of the previous release %s, which is published: %s. Refusing to diff against an older release instead — that would compare against the wrong API surface and could under-bump a breaking change. Fix the package so its assembly loads from the NuGet cache, or use an explicit alpha/beta/rc/stable command."
                                    unreadableTag
                                    reason
                            )
                        )
                    | NoPublishedPrior ->
                        // Every prior tag is an orphan: nothing published to diff
                        // against, and so no breaking-change risk to guard (no consumer
                        // ever received those releases). Bump conservatively off the
                        // latest tag rather than aborting.
                        ownChangeBump NoChange
                    | BaselineFound(baselineVersion, oldApi) ->
                        let currentApi = input.ExtractCurrentApi pkg.DllPath
                        let apiChange = compare oldApi currentApi

                        // Fold the realized-CLI-grammar diff into the API diff (stronger
                        // bump wins). Only when BOTH the prior release and the current
                        // build yield a grammar (a CommandTree consumer with an
                        // unambiguous root) — otherwise the API diff alone governs.
                        let change =
                            match
                                input.ExtractPreviousGrammar pkg.Name (format baselineVersion),
                                input.ExtractCurrentGrammar pkg.DllPath
                            with
                            | Some previousGrammar, Some currentGrammar ->
                                Grammar.foldIntoApi apiChange (Grammar.compare previousGrammar currentGrammar)
                            | _ -> apiChange

                        ownChangeBump change
                | _ -> explicitBump OwnChange
        | FirstRelease ->
            match input.Command with
            | Auto ->
                // A first release has no prior tag to API-diff against, so the declared
                // fsproj <Version> is what ships. Forced to NeedsBump rather than
                // `toDecision`, which would call a fsproj already at the target version
                // AlreadyBumped and skip the changelog promotion; FirstRelease has no
                // prior tag, so this can never be an in-progress resume.
                match declaredFsprojVersion pkg with
                | None ->
                    printfn
                        "Skipping %s: first release needs a <Version> in %s (or run an explicit `alpha`)"
                        pkg.Name
                        pkg.Fsproj

                    None
                | Some v when input.Config.ReservedVersions.Contains(format v) ->
                    printfn "Warning: version %s is reserved, skipping %s (first release)" (format v) pkg.Name
                    None
                | Some v ->
                    printfn "Bumping %s: first release at declared version %s" pkg.Name (format v)
                    Some(NeedsBump(pkg, v, OwnChange))
            | _ -> explicitBump OwnChange

let private resumeAlreadyBumped
    (input: ReleaseInput)
    (graph: ReleaseOrder.ReleaseGraph)
    (alreadyBumped: (PackageConfig * Version) list)
    : int =
    printfn "\nResuming in-progress release (versions already bumped, tags not yet pushed):"

    for (pkg, version) in alreadyBumped do
        printfn "  %s: resuming in-progress release -> tag %s" pkg.Name (toTag pkg.TagPrefix version)

    match input.Mode with
    | DryRun -> 0
    | PushTags ->
        // Re-push main first: if the original run failed at `pushMain`, the bump
        // commit is still local-only here. `jj git push` is idempotent, so pushing
        // again is safe and closes the partial-failure window before tagging.
        pushMain input.Run

        for (pkg, version) in alreadyBumped do
            let tag = toTag pkg.TagPrefix version

            if not (tagExists input.Run tag) then
                tagRevision input.Run tag "main"

        waitForCiAndPushTags input graph alreadyBumped
    | LocalPublish -> packLocally input.Run alreadyBumped

/// The changelog bullet auto-inserted for a dependency-triggered rebundle bump
/// whose own `## Unreleased` section is missing or empty.
let internal rebundleChangelogBullet =
    "- chore: rebuild to bundle updated dependencies"

let private executeBumps
    (input: ReleaseInput)
    (graph: ReleaseOrder.ReleaseGraph)
    (needsBump: (PackageConfig * Version * BumpTrigger) list)
    (alreadyBumped: (PackageConfig * Version) list)
    : int =
    let allBumps = (needsBump |> List.map (fun (pkg, v, _) -> pkg, v)) @ alreadyBumped

    printfn "\nRelease plan:"

    for (pkg, version) in allBumps do
        printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

    match ReleaseOrder.waves graph (fun (pkg: PackageConfig, _) -> pkg.Name) allBumps with
    | [ _ ] -> ()
    | waves ->
        printfn "\nPublication order (each wave waits for the previous one to be on NuGet):"

        waves
        |> List.iteri (fun index wave ->
            printfn "  %d. %s" (index + 1) (wave |> List.map (fun (pkg, _) -> pkg.Name) |> String.concat ", "))

    // Only OwnChange bumps are planned: the same `promotionPlans` `--check` reads,
    // so the release writes what the check reported. A plan is an error only when
    // the section is unauthored AND nothing (commit summaries, dependency changes)
    // derives one. A DependencyChange (rebundle) bump's real change lives in the
    // dependency's changelog, so it gets the fixed rebundle bullet instead.
    let ownChangePlans =
        needsBump
        |> List.collect (fun (pkg, version, trigger) ->
            match trigger with
            | DependencyChange -> []
            | OwnChange ->
                let latestTag =
                    getSortedTags input.Run pkg.TagPrefix |> List.tryHead |> Option.map fst

                promotionPlans input pkg latestTag
                |> List.map (fun (pkgName, path, plan) -> pkgName, path, version, plan))

    let emptySectionErrors =
        ownChangePlans
        |> List.choose (fun (pkgName, _, _, plan) ->
            match plan with
            | Error err -> Some(pkgName, err)
            | Ok _ -> None)

    let promotions =
        ownChangePlans
        |> List.choose (fun (_, path, version, plan) ->
            match plan with
            | Ok plan -> Some(path, version, plan)
            | Error _ -> None)

    // Promotion turns `## Unreleased` into a version section, so a callout that
    // has sunk below the entries is about to be frozen there. Checked for every
    // bump regardless of trigger, and never suppressed.
    let changelogErrors =
        emptySectionErrors
        @ calloutOrderProblems input.Config (needsBump |> List.map (fun (pkg, _, _) -> pkg))

    match input.Mode with
    | DryRun ->
        for (pkgName, err) in changelogErrors do
            printfn "  Warning [%s]: %s" pkgName (Changelog.formatError err)

        0
    | _ when not changelogErrors.IsEmpty ->
        printfn "\nError: CHANGELOG validation failed. Aborting release before any writes."

        for (pkgName, err) in changelogErrors do
            printfn "  %s: %s" pkgName (Changelog.formatError err)

        1
    | mode ->
        for (pkg, version, _) in needsBump do
            updateFsprojVersion pkg.Fsproj version

            for extra in pkg.FsProjsSharingSameTag do
                updateFsprojVersion extra version

        let today = System.DateTime.Today

        for (path, version, plan) in promotions do
            Changelog.applyPromotion path version today plan

        for (pkg, version, trigger) in needsBump do
            if trigger = DependencyChange then
                for (_, path) in changelogPathsFor input.Config pkg do
                    Changelog.promoteOrInsert path version today rebundleChangelogBullet

        let versionSummary =
            allBumps
            |> List.map (fun (pkg, version) -> sprintf "%s %s" pkg.Name (format version))
            |> String.concat ", "

        commitAndAdvanceMain input.Run (sprintf "Bump versions: %s" versionSummary)

        match mode with
        | PushTags ->
            // Push the bump commit BEFORE creating any local tag. If the push
            // fails, no tag exists yet, so the next run's resume logic
            // (`inProgressResumeVersion`, which keys off "no tag at the fsproj
            // version") still fires and finishes the release. Tagging first would
            // leave an orphan local tag pointing at a commit that never reached the
            // remote, which the resume path treats as "already done".
            pushMain input.Run

            for (pkg, version) in allBumps do
                let tag = toTag pkg.TagPrefix version
                tagRevision input.Run tag "main"

            waitForCiAndPushTags input graph allBumps
        | LocalPublish -> packLocally input.Run allBumps
        | DryRun -> 0

/// The promise `--check` makes, stated with its limits so a pass is not read as
/// "the promoted changelog covers every commit". Pinned by tests.
let internal changelogCheckContract: string =
    "What this check verifies: every changed package has something to promote, and every consumer-visible \
     PackageReference change is recorded. An authored '## Unreleased' section is promoted as written: \
     commit summaries are not merged into an authored section (they are used only when the section is empty), \
     so covering the rest of the release is the author's job."

/// `--check`: fail (exit 1) when a package with own-source changes since its last
/// tag has nothing to promote — an unauthored `## Unreleased` that neither its
/// commit summaries nor its dependency changes derive. A pre-flight gate for
/// `mise run ci`. It never builds or diffs API. It reads the SAME
/// `promotionPlans` the release applies and prints them, so what it reports is
/// what promotion writes; a package with no prior tag, or no own-source change,
/// is not planned.
let private runChangelogCheck (input: ReleaseInput) (selectedPackages: PackageConfig list) : int =
    let plans =
        selectedPackages
        |> List.collect (fun pkg ->
            match getSortedTags input.Run pkg.TagPrefix |> List.tryHead with
            | None -> []
            | Some(tag, _) ->
                let ownSrcDir = System.IO.Path.GetDirectoryName pkg.Fsproj

                if hasChangesSinceTag input.Run tag ownSrcDir then
                    promotionPlans input pkg (Some tag)
                else
                    [])

    let problems =
        plans
        |> List.choose (fun (pkgName, _, plan) ->
            match plan with
            | Error err -> Some(pkgName, err)
            | Ok _ -> None)

    let promotions =
        plans
        |> List.choose (fun (pkgName, path, plan) ->
            match plan with
            | Ok plan -> Some(pkgName, path, plan)
            | Error _ -> None)

    if not promotions.IsEmpty then
        printfn "Release will promote:"

    for (pkgName, path, plan) in promotions do
        match plan.Source with
        | Changelog.Authored ->
            printfn "  %s (%s): the authored '## Unreleased' section, promoted as written" pkgName path

            if not plan.DependencyBullets.IsEmpty then
                printfn "    plus the consumer-visible dependency changes it does not name:"
        | Changelog.Derived bullets ->
            printfn "  %s (%s): '## Unreleased' is empty, so release writes these derived entries:" pkgName path

            for bullet in bullets do
                printfn "      %s" bullet

        for bullet in plan.DependencyBullets do
            printfn "      %s" bullet

    // The callout-order rule is checked for EVERY selected package (and the repo
    // root changelog), not only the changed ones: a merge buries a callout by
    // rewriting the changelog alone, with no source change to key off.
    let calloutProblems = calloutOrderProblems input.Config selectedPackages

    if problems.IsEmpty && calloutProblems.IsEmpty then
        printfn
            "Changelog check passed: every changed package has something to promote, and no '## Unreleased' callout is buried."

        printfn "%s" changelogCheckContract
        0
    else
        if not problems.IsEmpty then
            printfn
                "\nError: changelog check failed — changed package(s) have an empty '## Unreleased' and nothing to derive one from (no commit summaries, no dependency changes):"

            for (pkgName, err) in problems do
                printfn "  %s: %s" pkgName (Changelog.formatError err)

            printfn
                "Fix: add a '## Unreleased' entry, or give the commit(s) a conventional summary (feat:/fix:/chore:/...)."

        if not calloutProblems.IsEmpty then
            printfn
                "\nError: changelog check failed — a '## Unreleased' callout is no longer the section's first content:"

            for (pkgName, err) in calloutProblems do
                printfn "  %s: %s" pkgName (Changelog.formatError err)

        1

/// Main release orchestration
let release (input: ReleaseInput) : int =
    if input.Mode = DryRun then
        printfn "Dry run: no files will be modified and no tags will be created."

    match selectPackages input.TargetPackages input.Config.Packages with
    | Error msg ->
        printfn "Error: %s" msg
        1
    | Ok selectedPackages ->

        // `input.Config.Packages` is deliberately NOT narrowed to `selectedPackages`.
        // It is the repo's *structural* package set — it answers "is this a
        // single-package repo?" (changelog at root vs. per-fsproj-dir, see
        // `changelogPathsFor`) and "which projects are separately-released dependency
        // boundaries?". `--only` selects what to release; it must not rewrite repo
        // structure. So the selection is applied at the release iteration below.
        if not input.TargetPackages.IsEmpty then
            printfn "Targeting: %s" (selectedPackages |> List.map (fun p -> p.Name) |> String.concat ", ")

        if input.Check then
            runChangelogCheck input selectedPackages
        else

            match preReleaseChecks input with
            | Error code -> code
            | Ok() ->
                // Explicit modes (non-Auto) skip API diffing, so the build is only needed
                // when comparing the current assembly against the previously published one.
                let needsBuild = input.Mode <> DryRun || input.Command = Auto

                if needsBuild then
                    runPreBuild input

                // The publication order is derived from the WHOLE repo's packages, not the
                // `--only` selection: a dependency that is not being released still
                // links a dependent to a dependency behind it. Decided before any write,
                // so a cycle or an fsproj owned by two packages refuses the release while
                // nothing has changed.
                match ReleaseOrder.fromConfig input.Config with
                | Error reason ->
                    printfn "\nError: cannot order the release: %s Aborting before any writes." reason
                    1
                | Ok graph ->

                    let decisions = selectedPackages |> List.choose (decideBump input)

                    let cannotDetermine =
                        decisions
                        |> List.choose (function
                            | CannotDetermine(p, reason) -> Some(p, reason)
                            | _ -> None)

                    let needsBump =
                        decisions
                        |> List.choose (function
                            | NeedsBump(p, v, trigger) -> Some(p, v, trigger)
                            | _ -> None)

                    let alreadyBumped =
                        decisions
                        |> List.choose (function
                            | AlreadyBumped(p, v) -> Some(p, v)
                            | _ -> None)

                    if not cannotDetermine.IsEmpty then
                        printfn "\nError: cannot determine the version bump. Aborting before any writes."

                        for (pkg, reason) in cannotDetermine do
                            printfn "  %s: %s" pkg.Name reason

                        1
                    elif needsBump.IsEmpty && alreadyBumped.IsEmpty then
                        printfn "No packages to release"
                        0
                    elif needsBump.IsEmpty then
                        resumeAlreadyBumped input graph alreadyBumped
                    else
                        executeBumps input graph needsBump alreadyBumped

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
        /// Fetch a prior release's public API by (packageName, version). The
        /// tri-state lets Auto distinguish an orphan tag (`AbsentOnFeed` — walk
        /// back to an older published release) from a transient `FetchError`
        /// (abort rather than under-bump).
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
        /// whole point of AUTOMATION-711 is that it must be.
        TagPush: Vcs.TagPushPolicy
        /// Ask the feed whether a (packageName, version) is published. The
        /// three-valued answer is what lets an orphan tag be distinguished from an
        /// unreachable feed, so ONE seam serves both the post-push availability
        /// poll and the orphan-tag detection.
        CheckFeedPresence: string -> string -> FeedPresence
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

/// Poll NuGet until every (packageId, version) is restorable, or until
/// `maxAttempts` rounds elapse. Returns true when all packages are available,
/// false on timeout. `maxAttempts = 1` does exactly one check then times out.
/// AUTOMATION-355 — RETURNS THE PACKAGES IT COULD NOT CONFIRM, not a bool. Empty
/// means every package is on the feed.
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
let internal waitForNuGet
    (checkFeedPresence: string -> string -> FeedPresence)
    (pollIntervalMs: int)
    (maxAttempts: int)
    (packages: (string * string) list)
    : (string * string) list =
    let rec poll attempt pending =
        // Only a definite `OnFeed` clears a package from the poll: an unreachable
        // feed is not evidence of arrival, so keep waiting exactly as for absence.
        let stillPending =
            pending |> List.filter (fun (id, ver) -> checkFeedPresence id ver <> OnFeed)

        if List.isEmpty stillPending then
            []
        elif attempt + 1 >= maxAttempts then
            for id, ver in stillPending do
                printfn "Timed out waiting for %s %s on NuGet after %d attempts" id ver maxAttempts

            stillPending
        else
            for id, ver in stillPending do
                printfn "Waiting for %s %s on NuGet..." id ver

            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1) stillPending

    poll 0 packages

/// Report what the tag push and its confirmation actually established, and pick the
/// exit code that matches.
///
/// Three outcomes, three exit codes — the convention AUTOMATION-355 established for the
/// NuGet wait, applied to the same question one stage earlier:
///
/// * `0` — every tag has a workflow run (this function is not called).
/// * `1` — the release DEMONSTRABLY did not happen: a push failed, or a run exists and
///   has already finished without publishing. Both stop an ordered release chain.
/// * `2` — the tags are on the remote and no run has appeared yet. "I stopped waiting"
///   is not "it failed", and AUTOMATION-711 is the cost of confusing the two: three
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
        printfn "Error: %d pushed tag(s) have a workflow run that FAILED:" failedRuns.Length

        for tag, runs in failedRuns do
            for runInfo in runs do
                printfn
                    "  %s — %s finished %A (%s)"
                    tag
                    (if runInfo.Name = "" then "the workflow" else runInfo.Name)
                    runInfo.Conclusion
                    (if runInfo.Url = "" then "no url reported" else runInfo.Url)

        printfn ""
        printfn "This is a real failure, not a slow one: the run exists and it is finished, so nothing published."
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
        printfn "GitHub registers a tag-push run seconds after the push, and this poll can outrun it."
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

let private waitForCiAndPushTags (input: ReleaseInput) (bumps: (PackageConfig * Version) list) : int =
    let run = input.Run

    let tags = bumps |> List.map (fun (pkg, version) -> toTag pkg.TagPrefix version)

    let pkgVersions = bumps |> List.map (fun (pkg, version) -> pkg.Name, format version)

    printfn "Waiting for CI on the version-bump commit to pass before pushing the tag (expected, ~1-2 min)..."

    let ciStatus = waitForCi run input.CiPollIntervalMs input.CiMaxAttempts

    match ciStatus with
    | Passed ->
        // A tag can land on the remote and trigger no workflow at all (a batch push
        // does exactly that), so confirm a run exists rather than claiming a release
        // is happening.
        let unconfirmed = pushTagsAndConfirmDetailed run input.TagPush tags

        if not (List.isEmpty unconfirmed) then
            reportTagConfirmationFailures unconfirmed
        else

            printfn "Tags pushed, and a workflow run exists for each. GitHub Actions will handle the release."

            if input.WaitForNuGet then
                printfn "Waiting for NuGet to index the published package(s)..."

                let unconfirmed =
                    waitForNuGet input.CheckFeedPresence input.NuGetPollIntervalMs input.NuGetMaxAttempts pkgVersions

                if List.isEmpty unconfirmed then
                    0
                else
                    // AUTOMATION-355 — exit 2, and NOT 1. Three outcomes, three
                    // answers: 0 confirmed on the feed, 1 the publish demonstrably
                    // failed (CI red, tags not pushed), 2 the tags went and the
                    // packages have not appeared within the window.
                    //
                    // This used to `|> ignore` the result and return 0, so a
                    // release that gave up waiting reported success and the
                    // operator learned otherwise by diffing tags against
                    // nuget.org by hand.
                    //
                    // 2 rather than 1 because "I stopped waiting" is not "it
                    // failed": the packages may land minutes later, and calling
                    // that a failure would train people to re-run a release that
                    // already succeeded — a worse habit than the one being fixed.
                    printfn ""

                    printfn
                        "Release NOT CONFIRMED: %d package(s) did not appear on NuGet in time."
                        (List.length unconfirmed)

                    for id, ver in unconfirmed do
                        printfn "  unconfirmed: %s %s" id ver

                    printfn ""
                    printfn "The tags ARE pushed, so this is not a failed publish — the packages may still be indexing."
                    printfn "Check https://www.nuget.org/packages/<id>/<version> for each, and if the Release workflow"
                    printfn "failed rather than lagged, resume it with:  gh run rerun <id> --failed"
                    2
            else
                0
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

/// The baseline API to diff the current build against, having walked the release
/// tags newest-first looking for one whose package is actually published.
type private BaselineApi =
    /// Found a published prior release to diff against: the version it resolved to
    /// (so the same release's grammar can be fetched at the fold point) and its API
    /// surface.
    | BaselineFound of version: Version * api: ApiSignature list
    /// Every prior tag's package is genuinely absent on the feed (all orphan
    /// tags). There is no published prior to diff against, so the caller falls
    /// back to first-release handling rather than guessing or aborting.
    | NoPublishedPrior
    /// A transient/network/auth fetch error — the truth is unknown, so the caller
    /// MUST abort rather than risk under-bumping a breaking change. Carries the
    /// underlying restore-failure message so the abort can surface *why*.
    | BaselineFetchError of fetchError: string

/// Resolve the API surface to diff against in Auto mode. Walks `sortedTags`
/// (newest-first); for each tag whose package is `Found` we diff against it. When
/// the newest tag's package is `AbsentOnFeed` it is an orphan (the release's CI
/// publish never landed on NuGet) — log a warning naming it and walk to the
/// next-newest published tag. Any `FetchError` aborts immediately (a genuine
/// outage must never be silently skipped). Exhausting the list with only orphans
/// yields `NoPublishedPrior`.
let private resolveBaselineApi
    (input: ReleaseInput)
    (pkg: PackageConfig)
    (sortedTags: (string * Version) list)
    : BaselineApi =
    sortedTags
    |> List.tryPick (fun (tag, version) ->
        match input.ExtractPreviousApi pkg.Name (format version) with
        | Found api -> Some(BaselineFound(version, api))
        | FetchError msg -> Some(BaselineFetchError msg)
        | AbsentOnFeed ->
            printfn
                "Warning: %s package for tag %s is not on the feed (orphan tag — its release publish never landed on NuGet). Skipping it and diffing against the previous published release."
                pkg.Name
                tag

            None)
    |> Option.defaultValue NoPublishedPrior

/// Is the release tagged at `version` an ORPHAN — tagged, but its package never
/// landed on the feed?
///
/// This asks the FEED (`CheckFeedPresence`), not the API extractor, even though
/// `ExtractPreviousApi` also carries an `AbsentOnFeed`. The question is presence,
/// not shape, and the API extractor is a proxy with two failure modes of its own:
///
///   * A `PackAsTool` package can never be API-probed — a `PackageReference` to a
///     tool package fails NU1212, which classifies as `FetchError`. The API seam
///     can therefore NEVER say "absent" for a tool, and every dotnet tool
///     (including this one) would stay wedged.
///   * `AbsentOnFeed` is also what the extractor reports when the package IS
///     published but its DLL cannot be located inside the .nupkg (an analyzer
///     package, for one). Driving a REPUBLISH off that signal re-releases a
///     perfectly published version on every unrecognised package layout.
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

                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion change)))
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

                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion NoChange)))
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
                    | NoPublishedPrior ->
                        // Every prior tag is an orphan: nothing published to diff
                        // against, and so no breaking-change risk to guard (no consumer
                        // ever received those releases). Bump conservatively off the
                        // latest tag rather than aborting.
                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion NoChange)))
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

                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion change)))
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

let private resumeAlreadyBumped (input: ReleaseInput) (alreadyBumped: (PackageConfig * Version) list) : int =
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

        waitForCiAndPushTags input alreadyBumped
    | LocalPublish -> packLocally input.Run alreadyBumped

/// The changelog bullet auto-inserted for a dependency-triggered rebundle bump
/// whose own `## Unreleased` section is missing or empty.
let internal rebundleChangelogBullet =
    "- chore: rebuild to bundle updated dependencies"

let private executeBumps
    (input: ReleaseInput)
    (needsBump: (PackageConfig * Version * BumpTrigger) list)
    (alreadyBumped: (PackageConfig * Version) list)
    : int =
    let allBumps = (needsBump |> List.map (fun (pkg, v, _) -> pkg, v)) @ alreadyBumped

    printfn "\nRelease plan:"

    for (pkg, version) in allBumps do
        printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

    // For an OwnChange bump, the commit descriptions since the package's latest
    // tag (over its own + bundled-dependency dirs) are the raw material the
    // changelog is derived from when `## Unreleased` is empty. A first release
    // (no prior tag) has no "since last release" range, so it derives nothing and
    // falls back to requiring a hand-authored entry. DependencyChange bumps get
    // the fixed rebundle bullet, so they need no descriptions.
    let descriptionsFor (pkg: PackageConfig) (trigger: BumpTrigger) : string list =
        match trigger with
        | DependencyChange -> []
        | OwnChange ->
            match getSortedTags input.Run pkg.TagPrefix |> List.tryHead with
            | Some(tag, _) -> descriptionsSinceTag input.Run tag (packageChangeDirs input.Config pkg)
            | None -> []

    let bumpsWithChangelogs =
        needsBump
        |> List.map (fun (pkg, v, trigger) ->
            pkg, v, trigger, changelogPathsFor input.Config pkg, descriptionsFor pkg trigger)

    // Only OwnChange bumps are subject to `## Unreleased` enforcement, and an empty
    // section is an error ONLY when it also can't be derived from the commit
    // descriptions. A hand-authored section always passes; a derivable one is filled
    // in at promote time. A DependencyChange (rebundle) bump's real change lives in
    // the dependency's changelog, so its own section may be missing.
    let emptySectionErrors =
        bumpsWithChangelogs
        |> List.filter (fun (_, _, trigger, _, _) -> trigger = OwnChange)
        |> List.collect (fun (_, _, _, paths, descriptions) ->
            let derivable = not (Changelog.deriveUnreleasedBullets descriptions |> List.isEmpty)

            paths
            |> List.choose (fun (pkgName, path) ->
                match Changelog.validateUnreleased path with
                | Ok() -> None
                | Error err -> if derivable then None else Some(pkgName, err)))

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

        for (_, version, trigger, paths, descriptions) in bumpsWithChangelogs do
            for (_, path) in paths do
                match trigger with
                | OwnChange ->
                    // Never clobbers a hand-authored entry; derives from commits
                    // when empty. Pre-validated above, so the Error (empty AND not
                    // derivable) branch is unreachable — fall back defensively to
                    // the rebundle placeholder rather than crash.
                    match Changelog.promoteOrDerive path version today descriptions with
                    | Ok() -> ()
                    | Error _ -> Changelog.promoteOrInsert path version today rebundleChangelogBullet
                | DependencyChange -> Changelog.promoteOrInsert path version today rebundleChangelogBullet

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

            waitForCiAndPushTags input allBumps
        | LocalPublish -> packLocally input.Run allBumps
        | DryRun -> 0

/// `--check`: fail (exit 1) when a package with own-source changes since its last
/// tag has an empty/missing `## Unreleased` that ALSO can't be derived from its
/// commit descriptions. A pre-flight gate for `mise run ci` so an unnotable change
/// is caught at PR time, not at release. It never builds or diffs API, and is
/// conservative: a package with no prior tag, or whose section is authored or
/// derivable, passes. Mirrors the release-time enforcement in `executeBumps`.
let private runChangelogCheck (input: ReleaseInput) (selectedPackages: PackageConfig list) : int =
    let problems =
        selectedPackages
        |> List.collect (fun pkg ->
            match getSortedTags input.Run pkg.TagPrefix |> List.tryHead with
            | None -> []
            | Some(tag, _) ->
                let ownSrcDir = System.IO.Path.GetDirectoryName pkg.Fsproj

                if not (hasChangesSinceTag input.Run tag ownSrcDir) then
                    []
                else
                    let descriptions =
                        descriptionsSinceTag input.Run tag (packageChangeDirs input.Config pkg)

                    let derivable = not (Changelog.deriveUnreleasedBullets descriptions |> List.isEmpty)

                    changelogPathsFor input.Config pkg
                    |> List.choose (fun (pkgName, path) ->
                        match Changelog.validateUnreleased path with
                        | Ok() -> None
                        | Error err -> if derivable then None else Some(pkgName, err)))

    // The callout-order rule is checked for EVERY selected package (and the repo
    // root changelog), not only the changed ones: a merge buries a callout by
    // rewriting the changelog alone, with no source change to key off.
    let calloutProblems = calloutOrderProblems input.Config selectedPackages

    if problems.IsEmpty && calloutProblems.IsEmpty then
        printfn
            "Changelog check passed: every changed package has an Unreleased entry (authored or derivable from commits), and no '## Unreleased' callout is buried."

        0
    else
        if not problems.IsEmpty then
            printfn
                "\nError: changelog check failed — changed package(s) have an empty '## Unreleased' with no commit descriptions to derive from:"

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
                    resumeAlreadyBumped input alreadyBumped
                else
                    executeBumps input needsBump alreadyBumped

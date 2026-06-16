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
        CiPollIntervalMs: int
        CiMaxAttempts: int
        CheckPublished: string -> string -> bool
        WaitForNuGet: bool
        NuGetPollIntervalMs: int
        NuGetMaxAttempts: int
        /// Opt-in (`--push`): when the release commit isn't on the remote yet,
        /// push it and wait for its CI before proceeding, instead of failing
        /// fast. Default `false` keeps auto-push off, because pushing to a
        /// branch-protected / PR-gated `main` is unsafe to do implicitly.
        Push: bool
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
        // RC + any API change -> revert to beta
        match change with
        | NoChange -> toStable current // promote to stable
        | _ -> toBeta current
    | PreRelease pre ->
        // Alpha/Beta: just increment pre-release number
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
            printfn "Waiting for CI to start..."
            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1)
        | InProgress runs when attempt < maxAttempts ->
            let completed =
                runs |> List.filter (fun r -> r.Status = Vcs.Completed) |> List.length

            printfn "Waiting for CI... (%d/%d runs complete)" completed runs.Length
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
/// Convenience only — callers MUST NOT fail the release on a false result, the
/// tags are already pushed.
let internal waitForNuGet
    (checkPublished: string -> string -> bool)
    (pollIntervalMs: int)
    (maxAttempts: int)
    (packages: (string * string) list)
    : bool =
    let rec poll attempt pending =
        let stillPending =
            pending |> List.filter (fun (id, ver) -> not (checkPublished id ver))

        if List.isEmpty stillPending then
            true
        elif attempt + 1 >= maxAttempts then
            for id, ver in stillPending do
                printfn "Timed out waiting for %s %s on NuGet after %d attempts" id ver maxAttempts

            false
        else
            for id, ver in stillPending do
                printfn "Waiting for %s %s on NuGet..." id ver

            System.Threading.Thread.Sleep(pollIntervalMs)
            poll (attempt + 1) stillPending

    poll 0 packages

let private waitForCiAndPushTags (input: ReleaseInput) (bumps: (PackageConfig * Version) list) : int =
    let run = input.Run

    let tags = bumps |> List.map (fun (pkg, version) -> toTag pkg.TagPrefix version)

    let pkgVersions = bumps |> List.map (fun (pkg, version) -> pkg.Name, format version)

    printfn "Waiting for CI on version bump commit..."
    let ciStatus = waitForCi run input.CiPollIntervalMs input.CiMaxAttempts

    match ciStatus with
    | Passed ->
        pushTags run tags
        printfn "Tags pushed. GitHub Actions will handle the release."

        if input.WaitForNuGet then
            printfn "Waiting for NuGet to index the published package(s)..."

            waitForNuGet input.CheckPublished input.NuGetPollIntervalMs input.NuGetMaxAttempts pkgVersions
            |> ignore

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
        runOrFail run "dotnet" (sprintf "pack %s -c Release -o artifacts/" pkg.Fsproj)
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

/// The actionable error printed when the release commit isn't on the remote, so
/// no CI run could ever exist for it. Names the fix and points at `--push` —
/// crucially, it never mislabels a *missing* run as a *failed* one (the old
/// behaviour, which ran the full local CI first and then reported "CI failed for
/// non-coverage reasons"). Kept as a value so the wording is pinned by one test.
let internal notPushedMessage: string =
    "Error: the release commit isn't on the remote, so no CI run exists for it (it \
     hasn't been pushed yet).\n\
     loosen-from-ci needs the commit's CI coverage artifact (to reconcile the \
     Linux-CI vs local coverage floors), so the commit must be pushed and its CI \
     must finish first.\n\
     Push the branch and wait for CI, then re-run the release — or pass --push to \
     push and wait for CI automatically."

/// Wait for the release commit's CI to complete and translate the terminal
/// status into a release verdict. Reused for both "already pushed" and
/// "just pushed via --push" — the single place that polls CI for the release
/// commit and decides go / no-go:
///
///   * `Passed`     -> proceed.
///   * `Failed`     -> the REAL failure case: a run exists and failed. Error
///                     "CI failed", naming each failing run's URL.
///   * `NoRuns`     -> polled to timeout and no run ever registered (rare on a
///                     pushed commit). Surface it honestly, distinct from a push
///                     precondition.
///   * `InProgress` -> still running after the timeout; re-run later.
///   * `Unknown`    -> couldn't read CI status (e.g. `gh` missing).
let private waitForReleaseCi (input: ReleaseInput) : Result<unit, int> =
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
/// Splits the historical "no CI run" failure into its two real causes:
///
///   * commit NOT on the remote  -> with `--push`, push then wait for CI;
///                                  otherwise FAIL FAST with `notPushedMessage`
///                                  (don't run anything expensive first).
///   * commit on the remote      -> WAIT for its CI to register/finish and
///                                  classify the result (`waitForReleaseCi`).
///                                  This covers the right-after-push race where
///                                  the run isn't registered yet — we poll for it
///                                  rather than erroring, exactly as the bump
///                                  commit's CI is already waited on.
///
/// A normal `release` after a push therefore just waits for CI itself; the user
/// never hand-rolls a `gh run watch` loop.
let private confirmReleaseCommitCiGreen (input: ReleaseInput) : Result<unit, int> =
    match releaseCommitSha input.Run with
    | None ->
        printfn "Error: could not determine the release commit (no VCS sha). Cannot verify CI before releasing."
        Error 1
    | Some sha ->
        if isCommitPushed input.Run sha then
            // On the remote already: a run will exist or is on its way — wait for it.
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
            // Fail-fast precondition FIRST: confirm the release commit is pushed
            // and its CI is green before any expensive coverage reconciliation.
            // Only once green do we reconcile coverage floors from the CI artifact.
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

/// Detect a release that was bumped but never tagged (a mid-release failure
/// between the version-bump commit and the CI-poll/tag step).
///
/// This is decided purely from the *desired end state*, never from
/// work-remaining (a half-rolled changelog, the latest-tag-to-HEAD diff, or an
/// API comparison): the version recorded in the fsproj is the intended release
/// version, and a release is finished only once its tag exists. So a release is
/// IN PROGRESS exactly when the fsproj `<Version>` is strictly ahead of the
/// latest published tag AND no tag yet exists at `<prefix><fsprojVersion>`.
///
/// When that holds we return the fsproj version so the caller resumes from the
/// CI-poll + tag step (idempotent finish) instead of recomputing a fresh bump,
/// re-rolling the changelog, or aborting on an unreadable previous API. A fully
/// released repo (fsproj == latest tag) and a fresh repo (fsproj == the next
/// computed bump, no intermediate bump-but-untag) both fall through to the
/// normal path.
let private inProgressResumeVersion (input: ReleaseInput) (state: ReleaseState) (pkg: PackageConfig) : Version option =
    match state with
    | FirstRelease -> None
    | HasPreviousRelease latestTagVersion ->
        // Read the fsproj lazily and tolerantly: a missing/unreadable/unversioned
        // fsproj is not a resume — defer to the normal path (which surfaces the
        // real error or skips the package). Only an existing version strictly
        // ahead of the latest tag, with no tag at that version, is in-progress.
        let fsprojVersion =
            if System.IO.File.Exists pkg.Fsproj then
                readFsprojVersion pkg.Fsproj
            else
                None

        match fsprojVersion with
        | Some v when
            sortKey v > sortKey latestTagVersion
            && not (tagExists input.Run (toTag pkg.TagPrefix v))
            ->
            Some v
        | _ -> None

/// The baseline API to diff the current build against, having walked the release
/// tags newest-first looking for one whose package is actually published.
type private BaselineApi =
    /// Found a published prior release to diff against (its API surface).
    | BaselineFound of ApiSignature list
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
    // tryPick stops at the first tag that resolves to a verdict (Found or a fetch
    // error); an AbsentOnFeed orphan warns and yields None so the walk continues.
    // Exhausting the list with only orphans leaves NoPublishedPrior.
    sortedTags
    |> List.tryPick (fun (tag, version) ->
        match input.ExtractPreviousApi pkg.Name (format version) with
        | Found api -> Some(BaselineFound api)
        | FetchError msg -> Some(BaselineFetchError msg)
        | AbsentOnFeed ->
            printfn
                "Warning: %s package for tag %s is not on the feed (orphan tag — its release publish never landed on NuGet). Skipping it and diffing against the previous published release."
                pkg.Name
                tag

            None)
    |> Option.defaultValue NoPublishedPrior

let private decideBump (input: ReleaseInput) (pkg: PackageConfig) : BumpDecision option =
    let sortedTags = getSortedTags input.Run pkg.TagPrefix

    let state =
        match sortedTags with
        | (_, version) :: _ -> HasPreviousRelease version
        | [] -> FirstRelease

    let ownSrcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)

    // A referenced project contributes to this package's change-detection
    // closure only if its DLL actually ships inside the package. The set of
    // separately-released packages comes from the configured `Packages`: each
    // such project is consumed as a NuGet `<dependency>` (not bundled) by a
    // library, so it's a dependency boundary. (PackAsTool packages bundle
    // everything regardless — handled inside `transitiveBundledRefDirs`.)
    let separatelyReleased =
        input.Config.Packages
        |> List.map (fun p -> p.Fsproj.Replace('\\', '/'))
        |> Set.ofList

    let isSeparatelyReleased (fsprojRel: string) =
        separatelyReleased.Contains(fsprojRel.Replace('\\', '/'))

    let depDirs =
        transitiveBundledRefDirs input.Config.RootDir pkg.Fsproj isSeparatelyReleased

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
                printfn "Skipping %s: no changes since %s" pkg.Name tag
                None
            | false, true ->
                match input.Command with
                | Auto -> depBumpAuto currentVersion tag
                | _ ->
                    printfn "Bumping %s: bundled dependency changed since %s (rebundle)" pkg.Name tag
                    explicitBump DependencyChange
            | true, _ ->
                // The package's own source changed — existing behaviour unchanged.
                match input.Command with
                | Auto ->
                    // Diff against the most recent *published* prior release,
                    // walking back past any orphan tags (whose package never landed
                    // on NuGet) so a missed publish doesn't block the next release.
                    match resolveBaselineApi input pkg sortedTags with
                    | BaselineFetchError msg ->
                        // The previous release's API couldn't be read because the feed
                        // was unreachable. Treating this as "no change" would let a
                        // breaking release ship as a patch (the very bug this guards
                        // against), so refuse to guess — and surface the underlying
                        // restore error so the cause is visible.
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
                        // against. There is no breaking-change risk to guard (no
                        // consumer ever received those releases), so auto-bump
                        // conservatively (NoChange) off the latest tag's version,
                        // exactly as the dependency-rebundle terminal (`depBumpAuto`)
                        // does, rather than aborting or demanding an explicit command.
                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion NoChange)))
                    | BaselineFound oldApi ->
                        let currentApi = input.ExtractCurrentApi pkg.DllPath
                        let change = compare oldApi currentApi
                        Some(toDecision OwnChange (skipReserved (determineBump currentVersion change)))
                | _ -> explicitBump OwnChange
        | FirstRelease ->
            match input.Command with
            | Auto -> None // Need explicit command for first release
            | _ -> explicitBump OwnChange

let private resumeAlreadyBumped (input: ReleaseInput) (alreadyBumped: (PackageConfig * Version) list) : int =
    printfn "\nResuming in-progress release (versions already bumped, tags not yet pushed):"

    for (pkg, version) in alreadyBumped do
        printfn "  %s: resuming in-progress release -> tag %s" pkg.Name (toTag pkg.TagPrefix version)

    match input.Mode with
    | DryRun -> 0
    | PushTags ->
        // Re-push main first: if the original run failed at `pushMain` (after the
        // bump commit was made locally but before it reached the remote), the bump
        // commit is still local-only here. `jj git push` is idempotent — a no-op
        // when main is already pushed (the CI-flake-after-push case) — so pushing
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

    let bumpsWithChangelogs =
        needsBump
        |> List.map (fun (pkg, v, trigger) -> pkg, v, trigger, changelogPathsFor input.Config pkg)

    // Only OwnChange bumps are subject to strict `## Unreleased` validation. A
    // DependencyChange (rebundle) bump's real change lives in the dependency's
    // changelog, so a missing/empty own section is fine — it's auto-filled at
    // promote time.
    let changelogErrors =
        bumpsWithChangelogs
        |> List.filter (fun (_, _, trigger, _) -> trigger = OwnChange)
        |> List.collect (fun (_, _, _, paths) -> paths)
        |> List.choose (fun (pkgName, path) ->
            match Changelog.validateUnreleased path with
            | Ok() -> None
            | Error err -> Some(pkgName, err))

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

        for (_, version, trigger, paths) in bumpsWithChangelogs do
            for (_, path) in paths do
                match trigger with
                | OwnChange -> Changelog.promoteUnreleased path version today
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

/// Main release orchestration
let release (input: ReleaseInput) : int =
    if input.Mode = DryRun then
        printfn "Dry run: no files will be modified and no tags will be created."

    match selectPackages input.TargetPackages input.Config.Packages with
    | Error msg ->
        printfn "Error: %s" msg
        1
    | Ok selectedPackages ->

        // NB: we deliberately do NOT narrow `input.Config.Packages` to
        // `selectedPackages`. `Config.Packages` is the repo's *structural* package
        // set — it answers "is this a single-package repo?" (changelog at root vs.
        // per-fsproj-dir, see `changelogPathsFor`) and "which projects are
        // separately-released dependency boundaries?" (see `separatelyReleased`).
        // `--only` is a *runtime selection* of which packages to release; it must
        // not rewrite repo structure. So the selection is applied at the release
        // iteration below, and `Config.Packages` stays the full set throughout.
        if not input.TargetPackages.IsEmpty then
            printfn "Targeting: %s" (selectedPackages |> List.map (fun p -> p.Name) |> String.concat ", ")

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

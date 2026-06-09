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
        ExtractPreviousApi: string -> string -> ApiSignature list option
        ExtractCurrentApi: string -> ApiSignature list
        CiPollIntervalMs: int
        CiMaxAttempts: int
        CheckPublished: string -> string -> bool
        WaitForNuGet: bool
        NuGetPollIntervalMs: int
        NuGetMaxAttempts: int
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

let private runCiCheck (input: ReleaseInput) : Result<unit, int> =
    if hasCoverageRatchet input.Run then
        printfn "Using coverageratchet loosen-from-ci for CI check..."

        match input.Run "dotnet" "tool run coverageratchet loosen-from-ci" with
        | Success _ -> Ok()
        | Failure msg ->
            printfn "Error: coverageratchet loosen-from-ci failed"

            if msg <> "" then
                printfn "  %s" msg

            Error 1
    else
        match waitForCi input.Run input.CiPollIntervalMs input.CiMaxAttempts with
        | Passed -> Ok()
        | Failed runs ->
            printfn "Error: CI failed"

            for r in runs do
                printfn "  FAILED: %s — %s" r.Name r.Url

            Error 1
        | InProgress _ ->
            printfn "Error: CI still running after timeout"
            Error 1
        | NoRuns ->
            printfn "Error: no CI runs found for the current commit"
            Error 1
        | Unknown ->
            printfn "Error: could not determine CI status"
            Error 1

let private preReleaseChecks (input: ReleaseInput) : Result<unit, int> =
    match input.Mode with
    | DryRun -> Ok()
    | PushTags
    | LocalPublish ->
        if hasUncommittedChanges input.Run then
            printfn "Error: uncommitted changes detected"
            Error 1
        else
            runCiCheck input

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

let private decideBump (input: ReleaseInput) (pkg: PackageConfig) : BumpDecision option =
    let state =
        match getLatestTag input.Run pkg.TagPrefix with
        | Some tag ->
            let versionStr = tag.Substring(pkg.TagPrefix.Length)
            HasPreviousRelease(parse versionStr)
        | None -> FirstRelease

    let ownSrcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)
    let depDirs = transitiveProjectRefDirs input.Config.RootDir pkg.Fsproj

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

        // A dependency-triggered "rebundle" bump: the package's own source is
        // unchanged but a bundled `<ProjectReference>` changed. A bundled tool/exe
        // has no meaningful public API to diff (and ExtractPreviousApi would fail
        // -> CannotDetermine), so treat it as a NoChange-style bump, honouring the
        // existing reserved-version patch-skip.
        let depBumpAuto (currentVersion: Version) (tag: string) =
            let newVersion = determineBump currentVersion NoChange

            let newVersion =
                if input.Config.ReservedVersions.Contains(format newVersion) then
                    bumpPatch newVersion
                else
                    newVersion

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
                    match input.ExtractPreviousApi pkg.Name (format currentVersion) with
                    | None ->
                        // The previous release's API couldn't be read. Treating this as
                        // "no change" would let a breaking release ship as a patch (the
                        // very bug this guards against), so refuse to guess.
                        Some(
                            CannotDetermine(
                                pkg,
                                sprintf
                                    "could not read the public API of the previous release %s (package not in the NuGet cache and download failed — check network/feed access). Refusing to guess the version bump; re-run once the package is reachable, or use an explicit alpha/beta/rc/stable command."
                                    tag
                            )
                        )
                    | Some oldApi ->
                        let currentApi = input.ExtractCurrentApi pkg.DllPath
                        let change = compare oldApi currentApi
                        let newVersion = determineBump currentVersion change

                        let newVersion =
                            if input.Config.ReservedVersions.Contains(format newVersion) then
                                bumpPatch newVersion
                            else
                                newVersion

                        Some(toDecision OwnChange newVersion)
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
            for (pkg, version) in allBumps do
                let tag = toTag pkg.TagPrefix version
                tagRevision input.Run tag "main"

            pushMain input.Run
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

        let input =
            { input with
                Config =
                    { input.Config with
                        Packages = selectedPackages } }

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

            let decisions = input.Config.Packages |> List.choose (decideBump input)

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

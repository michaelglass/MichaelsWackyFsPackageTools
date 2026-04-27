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
    { Run: string -> string -> CommandResult
      Config: ToolConfig
      Command: ReleaseCommand
      Mode: ReleaseMode
      ExtractPreviousApi: string -> string -> ApiSignature list option
      ExtractCurrentApi: string -> ApiSignature list
      CiPollIntervalMs: int
      CiMaxAttempts: int }

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

let private waitForCiAndPushTags
    (run: string -> string -> CommandResult)
    (ciPollIntervalMs: int)
    (ciMaxAttempts: int)
    (tags: string list)
    : int =
    printfn "Waiting for CI on version bump commit..."
    let ciStatus = waitForCi run ciPollIntervalMs ciMaxAttempts

    match ciStatus with
    | Passed ->
        pushTags run tags
        printfn "Tags pushed. GitHub Actions will handle the release."
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

type BumpDecision =
    | NeedsBump of PackageConfig * Version
    | AlreadyBumped of PackageConfig * Version

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

let private decideBump (input: ReleaseInput) (pkg: PackageConfig) : BumpDecision option =
    let state =
        match getLatestTag input.Run pkg.TagPrefix with
        | Some tag ->
            let versionStr = tag.Substring(pkg.TagPrefix.Length)
            HasPreviousRelease(parse versionStr)
        | None -> FirstRelease

    let srcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)

    match state with
    | HasPreviousRelease currentVersion when
        not (hasChangesSinceTag input.Run (toTag pkg.TagPrefix currentVersion) srcDir)
        ->
        printfn "Skipping %s: no changes since %s" pkg.Name (toTag pkg.TagPrefix currentVersion)
        None
    | _ ->
        let newVersionOpt =
            match input.Command with
            | Auto ->
                match state with
                | FirstRelease -> None // Need explicit command for first release
                | HasPreviousRelease currentVersion ->
                    let change =
                        match input.ExtractPreviousApi pkg.Name (format currentVersion) with
                        | Some oldApi ->
                            let currentApi = input.ExtractCurrentApi pkg.DllPath
                            compare oldApi currentApi
                        | None -> NoChange

                    let newVersion = determineBump currentVersion change

                    let newVersion =
                        if input.Config.ReservedVersions.Contains(format newVersion) then
                            bumpPatch newVersion
                        else
                            newVersion

                    Some newVersion
            | _ ->
                match forCommand state input.Command with
                | Ok v ->
                    if input.Config.ReservedVersions.Contains(format v) then
                        printfn "Warning: version %s is reserved, skipping" (format v)
                        None
                    else
                        Some v
                | Error msg ->
                    printfn "%s for %s" msg pkg.Name
                    None

        newVersionOpt
        |> Option.map (fun newVersion ->
            let currentFsprojVersion = readFsprojVersion pkg.Fsproj

            if currentFsprojVersion = Some newVersion then
                AlreadyBumped(pkg, newVersion)
            else
                NeedsBump(pkg, newVersion))

let private resumeAlreadyBumped (input: ReleaseInput) (alreadyBumped: (PackageConfig * Version) list) : int =
    printfn "\nResuming release (versions already bumped):"

    for (pkg, version) in alreadyBumped do
        printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

    match input.Mode with
    | DryRun -> 0
    | PushTags ->
        let tags =
            alreadyBumped
            |> List.map (fun (pkg, version) ->
                let tag = toTag pkg.TagPrefix version

                if not (tagExists input.Run tag) then
                    tagRevision input.Run tag "main"

                tag)

        waitForCiAndPushTags input.Run input.CiPollIntervalMs input.CiMaxAttempts tags
    | LocalPublish -> packLocally input.Run alreadyBumped

let private executeBumps
    (input: ReleaseInput)
    (needsBump: (PackageConfig * Version) list)
    (alreadyBumped: (PackageConfig * Version) list)
    : int =
    let allBumps = needsBump @ alreadyBumped

    printfn "\nRelease plan:"

    for (pkg, version) in allBumps do
        printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

    let bumpsWithChangelogs =
        needsBump
        |> List.map (fun (pkg, v) -> pkg, v, changelogPathsFor input.Config pkg)

    let changelogErrors =
        bumpsWithChangelogs
        |> List.collect (fun (_, _, paths) -> paths)
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
        for (pkg, version) in needsBump do
            updateFsprojVersion pkg.Fsproj version

            for extra in pkg.FsProjsSharingSameTag do
                updateFsprojVersion extra version

        let today = System.DateTime.Today

        for (_, version, paths) in bumpsWithChangelogs do
            for (_, path) in paths do
                Changelog.promoteUnreleased path version today

        let versionSummary =
            allBumps
            |> List.map (fun (pkg, version) -> sprintf "%s %s" pkg.Name (format version))
            |> String.concat ", "

        commitAndAdvanceMain input.Run (sprintf "Bump versions: %s" versionSummary)

        match mode with
        | PushTags ->
            let tags =
                allBumps
                |> List.map (fun (pkg, version) ->
                    let tag = toTag pkg.TagPrefix version
                    tagRevision input.Run tag "main"
                    tag)

            pushMain input.Run
            waitForCiAndPushTags input.Run input.CiPollIntervalMs input.CiMaxAttempts tags
        | LocalPublish -> packLocally input.Run allBumps
        | DryRun -> 0 // unreachable; matched above

/// Main release orchestration
let release (input: ReleaseInput) : int =
    if input.Mode = DryRun then
        printfn "Dry run: no files will be modified and no tags will be created."

    match preReleaseChecks input with
    | Error code -> code
    | Ok() ->
        // Explicit modes (non-Auto) skip API diffing, so the build is only needed
        // when comparing the current assembly against the previously published one.
        let needsBuild = input.Mode <> DryRun || input.Command = Auto

        if needsBuild then
            runPreBuild input

        let decisions = input.Config.Packages |> List.choose (decideBump input)

        let needsBump =
            decisions
            |> List.choose (function
                | NeedsBump(p, v) -> Some(p, v)
                | _ -> None)

        let alreadyBumped =
            decisions
            |> List.choose (function
                | AlreadyBumped(p, v) -> Some(p, v)
                | _ -> None)

        if needsBump.IsEmpty && alreadyBumped.IsEmpty then
            printfn "No packages to release"
            0
        elif needsBump.IsEmpty then
            resumeAlreadyBumped input alreadyBumped
        else
            executeBumps input needsBump alreadyBumped

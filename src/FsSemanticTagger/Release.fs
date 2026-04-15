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

type PublishMode =
    | GitHubActions
    | LocalPublish

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

/// Main release orchestration
let release
    (run: string -> string -> CommandResult)
    (config: ToolConfig)
    (cmd: ReleaseCommand)
    (mode: PublishMode)
    (extractPreviousApi: string -> string -> ApiSignature list option)
    (extractCurrentApi: string -> ApiSignature list)
    (ciPollIntervalMs: int)
    (ciMaxAttempts: int)
    : int =
    // 1. Check for uncommitted changes
    if hasUncommittedChanges run then
        printfn "Error: uncommitted changes detected"
        1
    else
        let ciOk =
            if hasCoverageRatchet run then
                printfn "Using coverageratchet loosen-from-ci for CI check..."

                match run "dotnet" "tool run coverageratchet loosen-from-ci" with
                | Success _ -> true
                | Failure _ -> false
            else
                match waitForCi run ciPollIntervalMs ciMaxAttempts with
                | Passed -> true
                | Failed runs ->
                    printfn "Error: CI failed"

                    for r in runs do
                        printfn "  FAILED: %s — %s" r.Name r.Url

                    false
                | InProgress _ ->
                    printfn "Error: CI still running after timeout"
                    false
                | NoRuns ->
                    printfn "Error: no CI runs found for the current commit"
                    false
                | Unknown ->
                    printfn "Error: could not determine CI status"
                    false

        if not ciOk then
            1
        else
            // 2. Run pre-build commands (e.g. paket restore)
            for preBuildCmd in config.PreBuildCmds do
                printfn "Running: %s" preBuildCmd
                let parts = preBuildCmd.Split(' ', 2)
                let cmd = parts[0]
                let args = if parts.Length > 1 then parts[1] else ""
                runOrFail run cmd args |> ignore

            // 3. Build in Release mode
            printfn "Building in Release mode..."
            runOrFail run "dotnet" "build -c Release" |> ignore

            // 4. For each package: determine release state and version bump
            let decisions =
                config.Packages
                |> List.choose (fun pkg ->
                    let state =
                        match getLatestTag run pkg.TagPrefix with
                        | Some tag ->
                            let versionStr = tag.Substring(pkg.TagPrefix.Length)
                            HasPreviousRelease(parse versionStr)
                        | None -> FirstRelease

                    // Skip packages with no changes since last tag
                    let srcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)

                    match state with
                    | HasPreviousRelease currentVersion when
                        not (hasChangesSinceTag run (toTag pkg.TagPrefix currentVersion) srcDir)
                        ->
                        printfn "Skipping %s: no changes since %s" pkg.Name (toTag pkg.TagPrefix currentVersion)
                        None
                    | _ ->

                        let newVersionOpt =
                            match cmd with
                            | Auto ->
                                match state with
                                | FirstRelease -> None // Need explicit command for first release
                                | HasPreviousRelease currentVersion ->
                                    let change =
                                        match extractPreviousApi pkg.Name (format currentVersion) with
                                        | Some oldApi ->
                                            let currentApi = extractCurrentApi pkg.DllPath
                                            compare oldApi currentApi
                                        | None -> NoChange

                                    let newVersion = determineBump currentVersion change

                                    let newVersion =
                                        if config.ReservedVersions.Contains(format newVersion) then
                                            bumpPatch newVersion
                                        else
                                            newVersion

                                    Some newVersion
                            | _ ->
                                match forCommand state cmd with
                                | Ok v ->
                                    if config.ReservedVersions.Contains(format v) then
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
                                NeedsBump(pkg, newVersion)))

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
                printfn "\nResuming release (versions already bumped):"

                for (pkg, version) in alreadyBumped do
                    printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

                // Create tags if they don't exist locally
                let tags =
                    alreadyBumped
                    |> List.map (fun (pkg, version) ->
                        let tag = toTag pkg.TagPrefix version

                        if not (tagExists run tag) then
                            tagRevision run tag "main"

                        tag)

                match mode with
                | GitHubActions -> waitForCiAndPushTags run ciPollIntervalMs ciMaxAttempts tags
                | LocalPublish -> packLocally run alreadyBumped
            else
                let allBumps = needsBump @ alreadyBumped

                // 5. Show summary
                printfn "\nRelease plan:"

                for (pkg, version) in allBumps do
                    printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

                // 6. Update fsproj versions (only for packages that need it)
                for (pkg, version) in needsBump do
                    updateFsprojVersion pkg.Fsproj version

                    for extra in pkg.FsProjsSharingSameTag do
                        updateFsprojVersion extra version

                // 7. Commit version bump and advance main bookmark
                let versionSummary =
                    allBumps
                    |> List.map (fun (pkg, version) -> sprintf "%s %s" pkg.Name (format version))
                    |> String.concat ", "

                commitAndAdvanceMain run (sprintf "Bump versions: %s" versionSummary)

                // 8. Tag main (the version bump commit) for each package
                let tags =
                    allBumps
                    |> List.map (fun (pkg, version) ->
                        let tag = toTag pkg.TagPrefix version
                        tagRevision run tag "main"
                        tag)

                // 9. Publish or push tags + main
                match mode with
                | GitHubActions ->
                    pushMain run
                    waitForCiAndPushTags run ciPollIntervalMs ciMaxAttempts tags
                | LocalPublish -> packLocally run allBumps

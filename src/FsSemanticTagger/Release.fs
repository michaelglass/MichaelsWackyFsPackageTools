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
    | HasPreviousRelease of tag: string * currentVersion: Version

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
let forCommand (state: ReleaseState) (cmd: ReleaseCommand) : Version option =
    match cmd, state with
    | StartAlpha, FirstRelease -> Some firstAlpha
    | StartAlpha, HasPreviousRelease(_, v) -> Some(nextAlphaCycle v)
    | PromoteToBeta, HasPreviousRelease(_, v) -> Some(toBeta v)
    | PromoteToRC, HasPreviousRelease(_, v) -> Some(toRC v)
    | PromoteToStable, HasPreviousRelease(_, v) -> Some(toStable v)
    | _ -> None

/// Update <Version> in an fsproj file
let updateFsprojVersion (fsprojPath: string) (version: Version) : unit =
    let content = System.IO.File.ReadAllText(fsprojPath)

    let pattern = System.Text.RegularExpressions.Regex("<Version>[^<]+</Version>")

    let newContent =
        pattern.Replace(content, sprintf "<Version>%s</Version>" (format version))

    System.IO.File.WriteAllText(fsprojPath, newContent)

let internal waitForCi (run: string -> string -> CommandResult) (pollIntervalMs: int) (maxAttempts: int) : CiStatus =
    let rec poll attempt =
        let status = getCiStatus run

        match status with
        | InProgress runs ->
            if attempt >= maxAttempts then
                printfn "Timed out waiting for CI after %d attempts" maxAttempts
                status
            else
                let completed = runs |> List.filter (fun r -> r.Status = "completed") |> List.length

                printfn "Waiting for CI... (%d/%d runs complete)" completed runs.Length
                System.Threading.Thread.Sleep(pollIntervalMs)
                poll (attempt + 1)
        | other -> other

    poll 0

/// Main release orchestration
let release
    (run: string -> string -> CommandResult)
    (config: ToolConfig)
    (cmd: ReleaseCommand)
    (mode: PublishMode)
    (extractPreviousApi: string -> string -> ApiSignature list option)
    (extractCurrentApi: string -> ApiSignature list)
    : int =
    // 1. Check for uncommitted changes
    if hasUncommittedChanges run then
        printfn "Error: uncommitted changes detected"
        1
    else
        let ciStatus = waitForCi run 15000 40

        match ciStatus with
        | Failed runs ->
            printfn "Error: CI failed"

            for r in runs do
                printfn "  FAILED: %s — %s" r.Name r.Url

            1
        | InProgress _ ->
            printfn "Error: CI still running after timeout"
            1
        | NoRuns ->
            printfn "Error: no CI runs found for the current commit"
            1
        | Unknown ->
            printfn "Error: could not determine CI status"
            1
        | Passed ->
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
            let bumps =
                config.Packages
                |> List.choose (fun pkg ->
                    let state =
                        match getLatestTag run pkg.TagPrefix with
                        | Some tag ->
                            let versionStr = tag.Substring(pkg.TagPrefix.Length)
                            HasPreviousRelease(tag, parse versionStr)
                        | None -> FirstRelease

                    // Skip packages with no changes since last tag
                    let srcDir = System.IO.Path.GetDirectoryName(pkg.Fsproj)

                    match state with
                    | HasPreviousRelease(tag, _) when not (hasChangesSinceTag run tag srcDir) ->
                        printfn "Skipping %s: no changes since %s" pkg.Name tag
                        None
                    | _ ->

                        match cmd with
                        | Auto ->
                            match state with
                            | FirstRelease -> None // Need explicit command for first release
                            | HasPreviousRelease(_tag, currentVersion) ->
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

                                Some(pkg, newVersion)
                        | _ ->
                            match forCommand state cmd with
                            | Some v ->
                                if config.ReservedVersions.Contains(format v) then
                                    printfn "Warning: version %s is reserved, skipping" (format v)
                                    None
                                else
                                    Some(pkg, v)
                            | None ->
                                printfn "Cannot %A from current state for %s" cmd pkg.Name
                                None)

            if bumps.IsEmpty then
                printfn "No packages to release"
                0
            else
                // 5. Show summary
                printfn "\nRelease plan:"

                for (pkg, version) in bumps do
                    printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

                // 6. Update fsproj versions
                for (pkg, version) in bumps do
                    updateFsprojVersion pkg.Fsproj version

                    for extra in pkg.FsProjsSharingSameTag do
                        updateFsprojVersion extra version

                // 7. Commit version bump and advance main bookmark
                let versionSummary =
                    bumps
                    |> List.map (fun (pkg, version) -> sprintf "%s %s" pkg.Name (format version))
                    |> String.concat ", "

                commitAndAdvanceMain run (sprintf "Bump versions: %s" versionSummary)

                // 8. Tag main (the version bump commit) for each package
                let tags =
                    bumps
                    |> List.map (fun (pkg, version) ->
                        let tag = toTag pkg.TagPrefix version
                        tagRevision run tag "main"
                        tag)

                // 9. Publish or push tags + main
                match mode with
                | GitHubActions ->
                    pushTags run tags
                    printfn "Tags pushed. GitHub Actions will handle the release."
                | LocalPublish ->
                    for (pkg, _version) in bumps do
                        runOrFail run "dotnet" (sprintf "pack %s -c Release -o artifacts/" pkg.Fsproj)
                        |> ignore

                        printfn "Packed: %s" pkg.Name
                // NuGet push would go here

                0

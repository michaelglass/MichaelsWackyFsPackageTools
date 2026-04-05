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

/// Main release orchestration
let release
    (run: string -> string -> CommandResult)
    (config: ToolConfig)
    (cmd: ReleaseCommand)
    (mode: PublishMode)
    : int =
    // 1. Check for uncommitted changes
    if hasUncommittedChanges run then
        printfn "Error: uncommitted changes detected"
        1
    elif not (isCiPassing run) then
        printfn "Error: CI is not passing for the current commit"
        1
    else
        // 2. Build in Release mode
        printfn "Building in Release mode..."
        runOrFail run "dotnet" "build -c Release" |> ignore

        // 3. For each package: determine release state and version bump
        let bumps =
            config.Packages
            |> List.choose (fun pkg ->
                let state =
                    match getLatestTag run pkg.TagPrefix with
                    | Some tag ->
                        let versionStr = tag.Substring(pkg.TagPrefix.Length)
                        HasPreviousRelease(tag, parse versionStr)
                    | None -> FirstRelease

                match cmd with
                | Auto ->
                    match state with
                    | FirstRelease -> None // Need explicit command for first release
                    | HasPreviousRelease(_tag, currentVersion) ->
                        // Compare API
                        let currentApi = extractFromAssembly pkg.DllPath
                        // For now, just bump patch (full API comparison from tag needs VCS workspace)
                        let _currentApi = currentApi
                        let change = NoChange
                        let newVersion = determineBump currentVersion change

                        if config.ReservedVersions.Contains(format newVersion) then
                            let newVersion = bumpPatch newVersion
                            Some(pkg, newVersion)
                        else
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
            // 4. Show summary
            printfn "\nRelease plan:"

            for (pkg, version) in bumps do
                printfn "  %s -> %s (tag: %s)" pkg.Name (format version) (toTag pkg.TagPrefix version)

            // 5. Update fsproj versions
            for (pkg, version) in bumps do
                updateFsprojVersion pkg.Fsproj version

                for extra in pkg.FsProjsSharingSameTag do
                    updateFsprojVersion extra version

            // 6. Commit and tag
            let tags =
                bumps |> List.map (fun (pkg, version) -> commitAndTag run pkg.TagPrefix version)

            // 7. Publish or push tags
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

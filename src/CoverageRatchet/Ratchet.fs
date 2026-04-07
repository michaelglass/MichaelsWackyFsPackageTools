module CoverageRatchet.Ratchet

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let ratchet (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let newOverrides =
        config.Overrides
        |> Map.toList
        |> List.choose (fun (name, ovr) ->
            match Map.tryFind name fileMap with
            | None -> Some(name, ovr)
            | Some file ->
                let newLine = max ovr.Line file.LinePct
                let newBranch = max ovr.Branch file.BranchPct

                if newLine >= config.DefaultLine && newBranch >= config.DefaultBranch then
                    None
                else
                    Some(
                        name,
                        { ovr with
                            Line = newLine
                            Branch = newBranch }
                    ))
        |> Map.ofList

    { config with Overrides = newOverrides }

type RatchetStatus =
    | NoChanges of Config
    | Tightened of Config
    | Failed of Config * failedFiles: string list

let ratchetWithStatus (config: Config) (files: FileCoverage list) : RatchetStatus =
    let failedFiles =
        buildFileResults config files
        |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)
        |> List.map (fun r -> r.File.FileName)

    let newConfig = ratchet config files

    if not (List.isEmpty failedFiles) then
        Failed(newConfig, failedFiles)
    elif newConfig.Overrides <> config.Overrides then
        Tightened newConfig
    else
        NoChanges config

let private mergeRawOverrides
    (raw: RawConfig)
    (resolvedBefore: Config)
    (resolvedAfter: Config)
    (newEntryPlatform: string option)
    : RawConfig =
    // Start with original raw overrides
    let mutable result = raw.RawOverrides

    // Handle files that were in the resolved config before
    for kv in resolvedBefore.Overrides do
        let name = kv.Key
        let existingEntries = Map.tryFind name raw.RawOverrides |> Option.defaultValue []

        match Map.tryFind name resolvedAfter.Overrides with
        | Some newOverride ->
            // File still has an override after processing - update the matching entry
            let updated =
                existingEntries
                |> List.map (fun entry ->
                    if entry.Platform = Some currentPlatform then
                        { entry with
                            Line = newOverride.Line
                            Branch = newOverride.Branch }
                    elif
                        entry.Platform = None
                        && not (existingEntries |> List.exists (fun e -> e.Platform = Some currentPlatform))
                    then
                        // This was the all-platform entry that resolved for us; update it in place
                        // only if there's no platform-specific entry
                        { entry with
                            Line = newOverride.Line
                            Branch = newOverride.Branch }
                    else
                        entry)

            result <- Map.add name updated result
        | None ->
            // File's override was removed (reached defaults) - remove only the current-platform entry
            let remaining =
                existingEntries
                |> List.filter (fun entry ->
                    if entry.Platform = Some currentPlatform then
                        false
                    elif
                        entry.Platform = None
                        && not (existingEntries |> List.exists (fun e -> e.Platform = Some currentPlatform))
                    then
                        // This was the all-platform entry that resolved for us; remove it
                        false
                    else
                        true)

            if List.isEmpty remaining then
                result <- Map.remove name result
            else
                result <- Map.add name remaining result

    // Handle files newly added by the resolved-after config (not in resolved-before)
    for kv in resolvedAfter.Overrides do
        if not (Map.containsKey kv.Key resolvedBefore.Overrides) then
            let existingEntries = Map.tryFind kv.Key raw.RawOverrides |> Option.defaultValue []

            let newEntry =
                { Line = kv.Value.Line
                  Branch = kv.Value.Branch
                  Reason = kv.Value.Reason
                  Platform = newEntryPlatform }

            result <- Map.add kv.Key (existingEntries @ [ newEntry ]) result

    { raw with RawOverrides = result }

let ratchetRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let ratcheted = ratchet resolved files
    mergeRawOverrides raw resolved ratcheted None

type RawRatchetStatus =
    | RawNoChanges of RawConfig
    | RawTightened of RawConfig
    | RawFailed of RawConfig * failedFiles: string list

let ratchetRawWithStatus (raw: RawConfig) (files: FileCoverage list) : RawRatchetStatus =
    let resolved = resolveConfig raw

    let failedFiles =
        buildFileResults resolved files
        |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)
        |> List.map (fun r -> r.File.FileName)

    let newRaw = ratchetRaw raw files

    if not (List.isEmpty failedFiles) then
        RawFailed(newRaw, failedFiles)
    elif newRaw.RawOverrides <> raw.RawOverrides then
        RawTightened newRaw
    else
        RawNoChanges raw

let loosen (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let updatedOverrides =
        config.Overrides
        |> Map.toList
        |> List.choose (fun (name, ovr) ->
            match Map.tryFind name fileMap with
            | None -> Some(name, ovr)
            | Some file ->
                if file.LinePct >= config.DefaultLine && file.BranchPct >= config.DefaultBranch then
                    None
                else
                    Some(
                        name,
                        { ovr with
                            Line = file.LinePct
                            Branch = file.BranchPct }
                    ))
        |> Map.ofList

    let newOverrides =
        files
        |> List.fold
            (fun acc file ->
                if Map.containsKey file.FileName acc then
                    acc
                elif file.LinePct >= config.DefaultLine && file.BranchPct >= config.DefaultBranch then
                    acc
                else
                    Map.add
                        file.FileName
                        { Line = file.LinePct
                          Branch = file.BranchPct
                          Reason = "loosened automatically"
                          Platform = None }
                        acc)
            updatedOverrides

    { config with Overrides = newOverrides }

let loosenRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let loosened = loosen resolved files
    mergeRawOverrides raw resolved loosened (Some currentPlatform)

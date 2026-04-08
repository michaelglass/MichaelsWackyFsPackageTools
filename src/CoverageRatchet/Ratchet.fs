module CoverageRatchet.Ratchet

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let private toThreshold (pct: float) = floor pct

let ratchet (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let newOverrides =
        config.Overrides
        |> Map.toList
        |> List.choose (fun (name, ovr) ->
            match Map.tryFind name fileMap with
            | None -> Some(name, ovr)
            | Some file ->
                let newLine = max ovr.Line (toThreshold file.LinePct)
                let newBranch = max ovr.Branch (toThreshold file.BranchPct)

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
    | NoChanges of RawConfig
    | Tightened of RawConfig
    | Failed of RawConfig * failedFiles: string list

let ratchetWithStatus (config: Config) (files: FileCoverage list) : RatchetStatus =
    let raw =
        { DefaultLine = config.DefaultLine
          DefaultBranch = config.DefaultBranch
          RawOverrides = config.Overrides |> Map.map (fun _ ovr -> [ ovr ]) }

    let failedFiles =
        buildFileResults config files
        |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)
        |> List.map (fun r -> r.File.FileName)

    let newConfig = ratchet config files

    let newRaw =
        { DefaultLine = newConfig.DefaultLine
          DefaultBranch = newConfig.DefaultBranch
          RawOverrides = newConfig.Overrides |> Map.map (fun _ ovr -> [ ovr ]) }

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newConfig.Overrides <> config.Overrides then
        Tightened newRaw
    else
        NoChanges raw

let private mergeRawOverrides
    (raw: RawConfig)
    (resolvedBefore: Config)
    (resolvedAfter: Config)
    (newEntryPlatform: string option)
    : RawConfig =
    let mutable result = raw.RawOverrides

    for kv in resolvedBefore.Overrides do
        let name = kv.Key
        let existingEntries = Map.tryFind name raw.RawOverrides |> Option.defaultValue []

        let hasPlatformSpecific =
            existingEntries |> List.exists (fun e -> e.Platform = Some currentPlatform)

        let isResolvingEntry (entry: Override) =
            entry.Platform = Some currentPlatform
            || (entry.Platform = None && not hasPlatformSpecific)

        match Map.tryFind name resolvedAfter.Overrides with
        | Some newOverride ->
            let updated =
                existingEntries
                |> List.map (fun entry ->
                    if isResolvingEntry entry then
                        { entry with
                            Line = newOverride.Line
                            Branch = newOverride.Branch }
                    else
                        entry)

            result <- Map.add name updated result
        | None ->
            let remaining =
                existingEntries |> List.filter (fun entry -> not (isResolvingEntry entry))

            if List.isEmpty remaining then
                result <- Map.remove name result
            else
                result <- Map.add name remaining result

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

let ratchetRawWithStatus (raw: RawConfig) (files: FileCoverage list) : RatchetStatus =
    let resolved = resolveConfig raw

    let failedFiles =
        buildFileResults resolved files
        |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)
        |> List.map (fun r -> r.File.FileName)

    let ratcheted = ratchet resolved files
    let newRaw = mergeRawOverrides raw resolved ratcheted None

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newRaw.RawOverrides <> raw.RawOverrides then
        Tightened newRaw
    else
        NoChanges raw

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
                            Line = toThreshold file.LinePct
                            Branch = toThreshold file.BranchPct }
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
                        { Line = toThreshold file.LinePct
                          Branch = toThreshold file.BranchPct
                          Reason = "loosened automatically"
                          Platform = None }
                        acc)
            updatedOverrides

    { config with Overrides = newOverrides }

let loosenRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let loosened = loosen resolved files
    mergeRawOverrides raw resolved loosened (Some currentPlatform)

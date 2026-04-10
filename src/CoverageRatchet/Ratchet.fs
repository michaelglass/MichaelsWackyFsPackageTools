module CoverageRatchet.Ratchet

open System.Text.Json
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
    | NoChanges
    | Tightened of RawConfig
    | Failed of RawConfig * failedFiles: string list

let ratchetWithStatus (config: Config) (files: FileCoverage list) : RatchetStatus =
    let failedFiles =
        buildFileResults config files
        |> List.filter (fun r -> not (FileResult.passed r))
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
        NoChanges

let private mergeRawOverrides
    (raw: RawConfig)
    (resolvedBefore: Config)
    (resolvedAfter: Config)
    (newEntryPlatform: Platform option)
    : RawConfig =
    let mutable result = raw.RawOverrides

    for kv in resolvedBefore.Overrides do
        let name = kv.Key
        let existingEntries = Map.tryFind name raw.RawOverrides |> Option.defaultValue []

        let hasPlatformSpecific =
            existingEntries |> List.exists (fun e -> e.Platform = Some Platform.current)

        let isResolvingEntry (entry: Override) =
            entry.Platform = Some Platform.current
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
        |> List.filter (fun r -> not (FileResult.passed r))
        |> List.map (fun r -> r.File.FileName)

    let ratcheted = ratchet resolved files
    let newRaw = mergeRawOverrides raw resolved ratcheted None

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newRaw.RawOverrides <> raw.RawOverrides then
        Tightened newRaw
    else
        NoChanges

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
                          Reason = Some "loosened automatically"
                          Platform = None }
                        acc)
            updatedOverrides

    { config with Overrides = newOverrides }

let loosenRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let loosened = loosen resolved files
    mergeRawOverrides raw resolved loosened (Some Platform.current)

let mergeFromCi (raw: RawConfig) (ciPlatform: Platform) (ciResults: Map<string, CiFileResult>) : RawConfig =
    let mutable result = raw.RawOverrides

    for kv in ciResults do
        let fileName = kv.Key
        let ciLine = kv.Value.Line
        let ciBranch = kv.Value.Branch

        if ciLine < raw.DefaultLine || ciBranch < raw.DefaultBranch then
            let existingEntries = Map.tryFind fileName result |> Option.defaultValue []

            let ciEntry =
                { Line = ciLine
                  Branch = ciBranch
                  Reason = None
                  Platform = Some ciPlatform }

            let hasPlatformEntries = existingEntries |> List.exists (fun e -> e.Platform.IsSome)

            let hasNonPlatformEntry =
                existingEntries |> List.exists (fun e -> e.Platform.IsNone)

            let newEntries =
                if List.isEmpty existingEntries then
                    [ ciEntry ]
                elif hasPlatformEntries then
                    let existsForCi =
                        existingEntries |> List.exists (fun e -> e.Platform = Some ciPlatform)

                    if existsForCi then
                        existingEntries
                        |> List.map (fun e ->
                            if e.Platform = Some ciPlatform then
                                { e with
                                    Line = ciLine
                                    Branch = ciBranch }
                            else
                                e)
                    else
                        existingEntries @ [ ciEntry ]
                elif hasNonPlatformEntry then
                    let localEntries =
                        existingEntries
                        |> List.map (fun e ->
                            { e with
                                Platform = Some Platform.current })

                    localEntries @ [ ciEntry ]
                else
                    existingEntries @ [ ciEntry ]

            result <- Map.add fileName newEntries result

    { raw with RawOverrides = result }

let parseCiThresholds (json: string) : Platform * Map<string, CiFileResult> =
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    let platformStr = root.GetProperty("platform").GetString()

    let platform = Platform.ofString platformStr |> Option.defaultValue Platform.current

    let resultsEl = root.GetProperty("results")

    let results =
        resultsEl.EnumerateObject()
        |> Seq.map (fun prop ->
            let line = prop.Value.GetProperty("line").GetDouble()
            let branch = prop.Value.GetProperty("branch").GetDouble()
            prop.Name, { Line = line; Branch = branch })
        |> Map.ofSeq

    platform, results

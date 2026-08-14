module CoverageRatchet.Ratchet

open System
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
        |> List.append (
            buildCountResults config files
            |> List.filter (fun r -> not (CountResult.passed r))
            |> List.map (fun r -> r.File.FileName)
        )
        |> List.distinct

    let newConfig = ratchet config files

    let newRaw =
        { DefaultLine = newConfig.DefaultLine
          DefaultBranch = newConfig.DefaultBranch
          RawOverrides = newConfig.Overrides |> Map.map (fun _ ovr -> [ ovr ])
          RawCountFloors = newConfig.CountFloors |> Map.map (fun _ floor -> [ floor ]) }

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newConfig.Overrides <> config.Overrides then
        Tightened newRaw
    else
        NoChanges

/// Fold a resolved before/after pair back into the platform-structured raw map,
/// touching only the entry that resolved for the running platform and leaving
/// other platforms' entries untouched.
///
/// Shared by percentage overrides and count floors so the two cannot drift apart:
/// `copyValues` is the only part that knows which numbers a floor kind carries.
let private mergeRawSection
    (platformOf: 'a -> Platform option)
    (setPlatform: Platform option -> 'a -> 'a)
    (copyValues: 'a -> 'a -> 'a)
    (rawEntries: Map<string, 'a list>)
    (before: Map<string, 'a>)
    (after: Map<string, 'a>)
    (newEntryPlatform: Platform option)
    : Map<string, 'a list> =
    let mutable result = rawEntries

    for kv in before do
        let name = kv.Key
        let existingEntries = Map.tryFind name rawEntries |> Option.defaultValue []

        let hasPlatformSpecific =
            existingEntries |> List.exists (fun e -> platformOf e = Some Platform.current)

        let isResolvingEntry entry =
            platformOf entry = Some Platform.current
            || (platformOf entry = None && not hasPlatformSpecific)

        match Map.tryFind name after with
        | Some updatedValue ->
            let updated =
                existingEntries
                |> List.map (fun entry ->
                    if isResolvingEntry entry then
                        copyValues updatedValue entry
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

    for kv in after do
        if not (Map.containsKey kv.Key before) then
            let existingEntries = Map.tryFind kv.Key rawEntries |> Option.defaultValue []
            let newEntry = setPlatform newEntryPlatform kv.Value
            result <- Map.add kv.Key (existingEntries @ [ newEntry ]) result

    result

let private mergeOverrideSection =
    mergeRawSection (fun (o: Override) -> o.Platform) (fun p o -> { o with Platform = p }) (fun src tgt ->
        { tgt with
            Line = src.Line
            Branch = src.Branch })

let private mergeCountFloorSection =
    mergeRawSection (fun (f: CountFloor) -> f.Platform) (fun p f -> { f with Platform = p }) (fun src tgt ->
        { tgt with
            CoveredLines = src.CoveredLines
            CoveredBranches = src.CoveredBranches })

let private mergeRawOverrides
    (raw: RawConfig)
    (resolvedBefore: Config)
    (resolvedAfter: Config)
    (newEntryPlatform: Platform option)
    : RawConfig =
    { raw with
        RawOverrides =
            mergeOverrideSection raw.RawOverrides resolvedBefore.Overrides resolvedAfter.Overrides newEntryPlatform }

let private mergeRawCountFloors
    (raw: RawConfig)
    (resolvedBefore: Config)
    (resolvedAfter: Config)
    (newEntryPlatform: Platform option)
    : RawConfig =
    { raw with
        RawCountFloors =
            mergeCountFloorSection
                raw.RawCountFloors
                resolvedBefore.CountFloors
                resolvedAfter.CountFloors
                newEntryPlatform }

// ---------------------------------------------------------------------------
// Count floors (AUTOMATION-119)
//
// Gating on the NUMERATOR. Covered-line count is stable for unchanged code
// because it does not divide by the JIT-dependent emitted-line set (ADR 0019).
//
// The cost, stated plainly: a count floor cannot tell a deleted TEST (a real
// regression) from deleted COVERED CODE (a legitimate refactor). Both lower the
// count. The only signal that would distinguish them is the total emitted-line
// count — precisely the quantity ADR 0019 proved unreliable — so guessing from
// it would reintroduce the non-determinism this design exists to avoid.
//
// Therefore a legitimate decrease is resolved by a HUMAN re-baseline, and the
// re-baseline is deliberately the same one-word command used to bootstrap
// (`baseline-lines`), so the routine path is the well-trodden path.
// ---------------------------------------------------------------------------

/// Raise count floors toward current counts. Monotonic — never lowers.
///
/// Only files that already HAVE a floor are touched: an ordinary ratchet run
/// must not silently enrol new files, or an impact-filtered partial run would
/// write floors from coverage that never ran.
let ratchetCountFloors (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let newFloors =
        config.CountFloors
        |> Map.map (fun name floor ->
            match Map.tryFind name fileMap with
            | None -> floor
            | Some file ->
                { floor with
                    CoveredLines = max floor.CoveredLines file.LinesCovered
                    CoveredBranches = max floor.CoveredBranches file.BranchesCovered })

    { config with CountFloors = newFloors }

/// Record current counts as the floor for every observed file.
///
/// This is both the bootstrap and the re-baseline, and it CAN lower a floor —
/// that is the point. Run it after deliberately removing covered code. Existing
/// reasons are preserved so a recorded justification is not silently dropped.
let baselineCountFloors (config: Config) (files: FileCoverage list) : Config =
    let newFloors =
        files
        |> List.fold
            (fun acc (file: FileCoverage) ->
                let reason =
                    Map.tryFind file.FileName acc |> Option.bind (fun (f: CountFloor) -> f.Reason)

                Map.add
                    file.FileName
                    { CoveredLines = file.LinesCovered
                      CoveredBranches = file.BranchesCovered
                      Reason = reason
                      Platform = None }
                    acc)
            config.CountFloors

    { config with CountFloors = newFloors }

let ratchetCountFloorsRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let ratcheted = ratchetCountFloors resolved files
    mergeRawCountFloors raw resolved ratcheted None

/// New entries are tagged with the running platform when the file already has
/// platform-specific entries; otherwise they are platform-less. Keeping the
/// platform explicit matters because CI is Linux-only, so a macOS-tagged floor
/// never runs there (AUTOMATION-213).
let baselineCountFloorsRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let baselined = baselineCountFloors resolved files
    mergeRawCountFloors raw resolved baselined None

let ratchetRaw (raw: RawConfig) (files: FileCoverage list) : RawConfig =
    let resolved = resolveConfig raw
    let ratcheted = ratchet resolved files

    mergeRawOverrides raw resolved ratcheted None
    |> fun r -> ratchetCountFloorsRaw r files

/// Files failing EITHER the percentage floors or their count floor.
let private allFailedFiles (resolved: Config) (files: FileCoverage list) =
    let pctFailures =
        buildFileResults resolved files
        |> List.filter (fun r -> not (FileResult.passed r))
        |> List.map (fun r -> r.File.FileName)

    let countFailures =
        buildCountResults resolved files
        |> List.filter (fun r -> not (CountResult.passed r))
        |> List.map (fun r -> r.File.FileName)

    pctFailures @ countFailures |> List.distinct

let ratchetRawWithStatus (raw: RawConfig) (files: FileCoverage list) : RatchetStatus =
    let resolved = resolveConfig raw
    let failedFiles = allFailedFiles resolved files

    let ratcheted = ratchet resolved files

    let newRaw =
        mergeRawOverrides raw resolved ratcheted None
        |> fun r -> ratchetCountFloorsRaw r files

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newRaw <> raw then
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
    mergeRawOverrides raw resolved loosened None

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
                        // loosen-from-ci must be monotonically non-raising: a floor for a given
                        // file+platform is only ever LOWERED toward the CI-measured value, never
                        // raised above it. Taking min per-metric prevents an anti-converging update
                        // (raising a floor above what CI stably measures, which guarantees the next
                        // CI run fails). Only the matching platform entry is touched, so platform
                        // sections stay isolated.
                        existingEntries
                        |> List.map (fun e ->
                            if e.Platform = Some ciPlatform then
                                { e with
                                    Line = min e.Line ciLine
                                    Branch = min e.Branch ciBranch }
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
    if String.IsNullOrWhiteSpace(json) then
        failwith
            "CI thresholds JSON is empty. Expected a coverage-thresholds artifact with shape \
             {\"platform\":\"linux|macos|windows\",\"results\":{\"File.fs\":{\"line\":N,\"branch\":N}}}."

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

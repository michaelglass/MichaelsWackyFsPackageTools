module CoverageRatchet.Thresholds

open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open CoverageRatchet.Cobertura

type Platform =
    | MacOS
    | Linux
    | Windows

module Platform =
    let current =
        if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
            MacOS
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
            Linux
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            Windows
        else
            MacOS // default to build platform

    let ofString (s: string) =
        match s.ToLowerInvariant() with
        | "macos" -> Some MacOS
        | "linux" -> Some Linux
        | "windows" -> Some Windows
        | _ -> None

    let toString =
        function
        | MacOS -> "macos"
        | Linux -> "linux"
        | Windows -> "windows"

// sync:threshold-types:start
/// A per-file PERCENTAGE floor. `Line` and `Branch` are percentages (0-100).
type Override =
    { Line: float
      Branch: float
      Reason: string option
      Platform: Platform option }

/// A per-file floor on the absolute COUNT of covered lines / covered branches.
///
/// Deliberately a separate type living in a separate config section
/// ("countFloors") from the percentage `Override` ("overrides"). The two are
/// never positionally interchangeable: `{"line": 93}` is 93 PERCENT, while
/// `{"coveredLines": 93}` is 93 LINES. A config written before count floors
/// existed has no "countFloors" section at all, so it can never be reread as
/// a set of counts.
///
/// Why counts: the coverage collector emits a source line only when its method
/// JIT-compiles, so the percentage denominator wobbles between runs while the
/// numerator does not (ADR 0019). Counts gate on the stable quantity.
type CountFloor =
    { CoveredLines: int
      CoveredBranches: int
      Reason: string option
      Platform: Platform option }

type Config =
    { DefaultLine: float
      DefaultBranch: float
      Overrides: Map<string, Override>
      CountFloors: Map<string, CountFloor> }
// sync:threshold-types:end

type RawConfig =
    { DefaultLine: float
      DefaultBranch: float
      RawOverrides: Map<string, Override list>
      RawCountFloors: Map<string, CountFloor list> }

let private defaultLineThreshold = 100.0
let private defaultBranchThreshold = 100.0

type FileResult =
    { File: FileCoverage
      LineThreshold: float
      BranchThreshold: float }

module FileResult =
    let linePassed (r: FileResult) = r.File.LinePct >= r.LineThreshold
    let branchPassed (r: FileResult) = r.File.BranchPct >= r.BranchThreshold
    let passed (r: FileResult) = linePassed r && branchPassed r

type CiFileResult = { Line: float; Branch: float }

type CheckResult =
    | AllPassed
    | SomeFailed of FileResult list

/// A file paired with the count floor it must clear.
/// Only files that HAVE a floor produce one of these.
type CountResult =
    { File: FileCoverage
      Floor: CountFloor }

module CountResult =
    let linesPassed (r: CountResult) =
        r.File.LinesCovered >= r.Floor.CoveredLines

    let branchesPassed (r: CountResult) =
        r.File.BranchesCovered >= r.Floor.CoveredBranches

    let passed (r: CountResult) = linesPassed r && branchesPassed r

/// Verdict of a count-floor check. `CountsAllPassed` covers "no file has a count
/// floor" as well as "every floor was cleared" — an absent floor is not a failure.
type CountCheckResult =
    | CountsAllPassed
    | CountsFailed of CountResult list

let private defaultConfig =
    { DefaultLine = defaultLineThreshold
      DefaultBranch = defaultBranchThreshold
      Overrides = Map.empty
      CountFloors = Map.empty }

let jsonOptions =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts

let buildFileResults (config: Config) (files: FileCoverage list) : FileResult list =
    files
    |> List.map (fun f ->
        let lineThreshold, branchThreshold =
            match Map.tryFind f.FileName config.Overrides with
            | Some ovr -> ovr.Line, ovr.Branch
            | None -> config.DefaultLine, config.DefaultBranch

        { File = f
          LineThreshold = lineThreshold
          BranchThreshold = branchThreshold })

let check (config: Config) (files: FileCoverage list) : CheckResult =
    let results = buildFileResults config files
    let failed = results |> List.filter (fun r -> not (FileResult.passed r))
    if List.isEmpty failed then AllPassed else SomeFailed failed

/// Pair each covered file with its count floor. Files with no floor recorded are
/// NOT count-checked — there is no meaningful universal default for an absolute
/// count, so count enforcement is opt-in per file via `baseline-lines`.
let buildCountResults (config: Config) (files: FileCoverage list) : CountResult list =
    files
    |> List.choose (fun f ->
        config.CountFloors
        |> Map.tryFind f.FileName
        |> Option.map (fun floor -> { File = f; Floor = floor }))

/// Check every file that has a count floor against it. Independent of `check`:
/// a file can clear its percentage floor and still fail its count floor, which is
/// the whole point — the percentage denominator moves, the count does not.
let checkCounts (config: Config) (files: FileCoverage list) : CountCheckResult =
    let results = buildCountResults config files
    let failed = results |> List.filter (fun r -> not (CountResult.passed r))

    if List.isEmpty failed then
        CountsAllPassed
    else
        CountsFailed failed

let private defaultRawConfig =
    { DefaultLine = defaultLineThreshold
      DefaultBranch = defaultBranchThreshold
      RawOverrides = Map.empty
      RawCountFloors = Map.empty }

/// Read the reason/platform pair shared by both floor kinds.
let private parseReason (el: JsonElement) =
    match el.TryGetProperty("reason") with
    | true, r ->
        let s = r.GetString()

        if isNull s || System.String.IsNullOrEmpty(s) then
            None
        else
            Some s
    | false, _ -> None

let private parsePlatform (el: JsonElement) =
    match el.TryGetProperty("platform") with
    | true, p ->
        let s = p.GetString()
        if isNull s then None else Platform.ofString s
    | false, _ -> None

let private parseOverrideElement (el: JsonElement) : Override =
    let line =
        if el.TryGetProperty("line") |> fst then
            el.GetProperty("line").GetDouble()
        else
            defaultLineThreshold

    let branch =
        if el.TryGetProperty("branch") |> fst then
            el.GetProperty("branch").GetDouble()
        else
            defaultBranchThreshold

    { Line = line
      Branch = branch
      Reason = parseReason el
      Platform = parsePlatform el }

/// Count floors use the keys "coveredLines"/"coveredBranches" — deliberately
/// NOT "line"/"branch", so a percentage entry can never be misread as a count.
/// A missing key means a floor of 0, i.e. "recorded but nothing to clear",
/// which is the honest reading for a file with no branches at all.
let private parseCountFloorElement (el: JsonElement) : CountFloor =
    let readInt (name: string) =
        match el.TryGetProperty(name) with
        | true, v -> v.GetInt32()
        | false, _ -> 0

    { CoveredLines = readInt "coveredLines"
      CoveredBranches = readInt "coveredBranches"
      Reason = parseReason el
      Platform = parsePlatform el }

let private parseSection (parseElement: JsonElement -> 'a) (root: JsonElement) (name: string) : Map<string, 'a list> =
    match root.TryGetProperty(name) with
    | false, _ -> Map.empty
    | true, sectionEl ->
        sectionEl.EnumerateObject()
        |> Seq.map (fun prop ->
            let entries =
                if prop.Value.ValueKind = JsonValueKind.Array then
                    prop.Value.EnumerateArray() |> Seq.map parseElement |> Seq.toList
                else
                    [ parseElement prop.Value ]

            prop.Name, entries)
        |> Map.ofSeq

let loadRawConfig (path: string) : RawConfig =
    if not (File.Exists(path)) then
        defaultRawConfig
    else
        let json = File.ReadAllText(path).Trim()

        if json = "{}" || System.String.IsNullOrWhiteSpace(json) then
            defaultRawConfig
        else
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            { defaultRawConfig with
                RawOverrides = parseSection parseOverrideElement root "overrides"
                RawCountFloors = parseSection parseCountFloorElement root "countFloors" }

/// Pick the entry for the running platform, falling back to a platform-less one.
/// A file whose only entries name OTHER platforms resolves to nothing and is
/// therefore unenforced here — which is why a macOS-only floor is invisible to a
/// Linux-only CI (AUTOMATION-213). Count floors share this rule on purpose:
/// one selection semantic, not two.
let private resolveForPlatform (platformOf: 'a -> Platform option) (entries: Map<string, 'a list>) : Map<string, 'a> =
    entries
    |> Map.toList
    |> List.choose (fun (name, candidates) ->
        let platformMatch =
            candidates |> List.tryFind (fun o -> platformOf o = Some Platform.current)

        let allMatch = candidates |> List.tryFind (fun o -> platformOf o = None)

        match platformMatch, allMatch with
        | Some m, _ -> Some(name, m)
        | None, Some m -> Some(name, m)
        | None, None -> None)
    |> Map.ofList

let resolveConfig (raw: RawConfig) : Config =
    { DefaultLine = raw.DefaultLine
      DefaultBranch = raw.DefaultBranch
      Overrides = resolveForPlatform (fun (o: Override) -> o.Platform) raw.RawOverrides
      CountFloors = resolveForPlatform (fun (f: CountFloor) -> f.Platform) raw.RawCountFloors }

let loadConfig (path: string) : Config = loadRawConfig path |> resolveConfig

/// Widen a platform-resolved `Config` back to a `RawConfig` whose every entry is
/// a one-element, platform-less list. The inverse of `resolveConfig` only for a
/// config that had no platform-specific entries to begin with — resolving
/// discards the other platforms' entries, and this cannot invent them back. Use
/// the `*Raw` merge functions when those entries must survive.
let toRawConfig (config: Config) : RawConfig =
    { DefaultLine = config.DefaultLine
      DefaultBranch = config.DefaultBranch
      RawOverrides = config.Overrides |> Map.map (fun _ ovr -> [ ovr ])
      RawCountFloors = config.CountFloors |> Map.map (fun _ floor -> [ floor ]) }

let private addReasonAndPlatform
    (reason: string option)
    (platform: Platform option)
    (entry: System.Collections.Generic.Dictionary<string, obj>)
    =
    match reason with
    | Some r -> entry.["reason"] <- r
    | None -> ()

    match platform with
    | Some p -> entry.["platform"] <- Platform.toString p
    | None -> ()

    entry

let private overrideToDict (ovr: Override) =
    let entry = System.Collections.Generic.Dictionary<string, obj>()
    entry.["line"] <- ovr.Line
    entry.["branch"] <- ovr.Branch
    addReasonAndPlatform ovr.Reason ovr.Platform entry

let private countFloorToDict (floor: CountFloor) =
    let entry = System.Collections.Generic.Dictionary<string, obj>()
    entry.["coveredLines"] <- floor.CoveredLines
    entry.["coveredBranches"] <- floor.CoveredBranches
    addReasonAndPlatform floor.Reason floor.Platform entry

/// Collapse a single platform-less entry to an object; keep anything else as an array.
let private sectionToDict (platformOf: 'a -> Platform option) (toDict: 'a -> _) (entries: Map<string, 'a list>) =
    let sectionDict = System.Collections.Generic.Dictionary<string, obj>()

    for kv in entries do
        match kv.Value with
        | [ single ] when platformOf single = None -> sectionDict.[kv.Key] <- toDict single
        | many -> sectionDict.[kv.Key] <- (many |> List.map toDict |> List.toArray)

    sectionDict

let saveRawConfig (path: string) (config: RawConfig) : unit =
    let dict = System.Collections.Generic.Dictionary<string, obj>()

    dict.["overrides"] <- sectionToDict (fun (o: Override) -> o.Platform) overrideToDict config.RawOverrides

    // Only emit the section when it has content, so configs that never adopted
    // count floors round-trip byte-identically.
    if not (Map.isEmpty config.RawCountFloors) then
        dict.["countFloors"] <- sectionToDict (fun (f: CountFloor) -> f.Platform) countFloorToDict config.RawCountFloors

    let json = JsonSerializer.Serialize(dict, jsonOptions)
    File.WriteAllText(path, json)

let saveConfig (path: string) (config: Config) : unit = saveRawConfig path (toRawConfig config)

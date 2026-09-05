module CoverageRatchet.Thresholds

open System.IO
open System.Runtime.InteropServices
open System.Text.Encodings.Web
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

/// The single options value both write paths share (`saveRawConfig` here and the
/// coverage-thresholds artifact in `CoverageRatchet/Program.fs`), so the two outputs
/// cannot encode the same text differently.
///
/// `Encoder` is load-bearing, not decoration. `JavaScriptEncoder.Default` escapes
/// everything outside Basic Latin — and `'`, `` ` ``, `"`, `<`, `>` besides — so a
/// `reason` the tool writes comes back as a six-character `\u2014` escape where the
/// same sentence typed by a human keeps its literal em-dash. Both parse to the same
/// string, so nothing ever failed and nothing ever warned: the file simply rewrote
/// itself on runs that moved no floor, and every
/// such rewrite is a conflict waiting in the ONE file this project forbids anyone to
/// resolve by hand (settle floors through a full run, never by picking a number). A
/// writer that is not a fixed point turns a policy about correctness into a chore
/// people learn to shortcut. AUTOMATION-151.
///
/// `UnsafeRelaxedJsonEscaping` is named for the single hazard it carries: its output
/// must not be dropped into HTML without further encoding. This output is a coverage
/// floor file, read by this tool and by reviewers and embedded in nothing, and the
/// property it must have instead is that writing it twice produces the same bytes.
let jsonOptions =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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

/// PURE: every file carrying a floor of either kind — the set a run is OBLIGED
/// to measure. Read from the CONFIG, never from the report; that is the whole
/// point of it existing separately.
let configuredFloors (config: Config) : Set<string> =
    Set.union
        (config.Overrides |> Map.toList |> List.map fst |> Set.ofList)
        (config.CountFloors |> Map.toList |> List.map fst |> Set.ofList)

/// A configured floor whose file has NO row in the coverage report.
///
/// Both floor kinds ride in one record because both are obligations the same
/// report failed to speak to. A caller that handled only percentages would
/// leave the identical hole open for counts.
type UnmeasuredFloor =
    { File: string
      HasPercentageFloor: bool
      HasCountFloor: bool }

/// PURE: every configured floor this report cannot speak to.
///
/// Floor keys are BASENAMES — `Cobertura.parseFiles` stores
/// `Path.GetFileName` — so this deliberately cannot separate "the file was
/// deleted from the tree" from "the file is still there and this run did not
/// measure it". There is no path to stat. Both are reported the same way and
/// the remedy text names both, because silently guessing between them is
/// exactly what the false pass was doing.
let unmeasuredFloors (config: Config) (files: FileCoverage list) : UnmeasuredFloor list =
    let measured = files |> List.map (fun f -> f.FileName) |> Set.ofList

    configuredFloors config
    |> Set.toList
    |> List.filter (fun name -> not (Set.contains name measured))
    |> List.map (fun name ->
        { File = name
          HasPercentageFloor = Map.containsKey name config.Overrides
          HasCountFloor = Map.containsKey name config.CountFloors })

/// AUTOMATION-127: what one `check` run is ENTITLED to conclude.
///
/// `CheckResult` and `CountCheckResult` above answer "did anything I looked at
/// fail?". That is the wrong question, and answering it was the bug: both take
/// their entire file set from the coverage report, so a report that shrinks
/// silently shrinks the claim with it. `7/7 files passed` is true of any 7,
/// including 7 out of 50 — which is the run this was filed over.
///
/// Completeness is carried IN THE TYPE rather than re-checked at each call
/// site. `AllFloorsHeld` has no field to put a hole in, so "everything held" is
/// not a sentence a partial report can form, and a renderer cannot reach the
/// passing case by forgetting to look at a list.
///
/// Rejected — keep the two results and add `if not (List.isEmpty unmeasured)
/// then fail` in `runCheck`. It fixes one call site and leaves `runCheckJson`
/// free to make the same mistake, which is how `runCheckJson` came to be a
/// second copy of the defect in the first place.
///
/// Rejected — a tolerance ("fail only when more than K% of floors are
/// unmeasured"). K is a number nobody can derive, and the honest value is zero:
/// a floor exists because someone measured that file, so a run that cannot see
/// it has not done the job the floor was written for.
type Verdict =
    /// Every file in the report met its floors, and every configured floor had
    /// a file to measure. The only passing case, and the only one with nowhere
    /// to put a hole.
    | AllFloorsHeld of measuredFiles: int
    /// The report carries no F# file at all. The purest form of the N/N pass
    /// this type exists to remove: a run that checked nothing, rendering
    /// exactly like one that checked everything.
    | NothingMeasured
    /// Everything the report could speak to held, and it could not speak to
    /// every configured floor. UNDETERMINABLE, which is not a pass.
    | Incomplete of measuredFiles: int * unmeasured: UnmeasuredFloor list
    /// At least one MEASURED file fell below a floor. The holes ride along so a
    /// red run cannot hide them behind the regression it is reporting.
    | BelowFloor of files: FileResult list * counts: CountResult list * unmeasured: UnmeasuredFloor list

/// PURE: the verdict, from the configuration and one run's report.
///
/// The expected set is the union of the two configured floor sections, never
/// the report's file list. That is the whole correction: the denominator has to
/// be the obligation, not the evidence.
///
/// Files in the report with no configured floor are still checked — they must
/// clear `DefaultLine`/`DefaultBranch` — so they can contribute a failure but
/// never a hole. A file counts as unmeasured only if something asked for it.
let judge (config: Config) (files: FileCoverage list) : Verdict =
    if List.isEmpty files then
        NothingMeasured
    else
        let fileFailures =
            buildFileResults config files
            |> List.filter (fun r -> not (FileResult.passed r))

        let countFailures =
            buildCountResults config files
            |> List.filter (fun r -> not (CountResult.passed r))

        let unmeasured = unmeasuredFloors config files

        match fileFailures, countFailures, unmeasured with
        | [], [], [] -> AllFloorsHeld files.Length
        | [], [], holes -> Incomplete(files.Length, holes)
        | fell, counts, holes -> BelowFloor(fell, counts, holes)

/// PURE: the process exit code for a verdict.
///
/// `1` is a floor that FELL — a measured regression, something a human changed.
/// `2` is "this run may not answer the question". Keeping them apart keeps "you
/// broke coverage" distinguishable from "you cannot know yet".
let exitCodeOf (verdict: Verdict) : int =
    match verdict with
    | AllFloorsHeld _ -> 0
    | BelowFloor _ -> 1
    | NothingMeasured
    | Incomplete _ -> 2

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

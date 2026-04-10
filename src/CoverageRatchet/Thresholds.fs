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

type Override =
    { Line: float
      Branch: float
      Reason: string option
      Platform: Platform option }

type Config =
    { DefaultLine: float
      DefaultBranch: float
      Overrides: Map<string, Override> }

type RawConfig =
    { DefaultLine: float
      DefaultBranch: float
      RawOverrides: Map<string, Override list> }

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

let private defaultConfig =
    { DefaultLine = defaultLineThreshold
      DefaultBranch = defaultBranchThreshold
      Overrides = Map.empty }

let internal jsonOptions =
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

let private defaultRawConfig =
    { DefaultLine = defaultLineThreshold
      DefaultBranch = defaultBranchThreshold
      RawOverrides = Map.empty }

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

    let reason =
        match el.TryGetProperty("reason") with
        | true, r ->
            let s = r.GetString()

            if isNull s || System.String.IsNullOrEmpty(s) then
                None
            else
                Some s
        | false, _ -> None

    let platform =
        match el.TryGetProperty("platform") with
        | true, p ->
            let s = p.GetString()
            if isNull s then None else Platform.ofString s
        | false, _ -> None

    { Line = line
      Branch = branch
      Reason = reason
      Platform = platform }

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

            match root.TryGetProperty("overrides") with
            | false, _ -> defaultRawConfig
            | true, overridesEl ->
                let overrides =
                    overridesEl.EnumerateObject()
                    |> Seq.map (fun prop ->
                        let entries =
                            if prop.Value.ValueKind = JsonValueKind.Array then
                                prop.Value.EnumerateArray() |> Seq.map parseOverrideElement |> Seq.toList
                            else
                                [ parseOverrideElement prop.Value ]

                        prop.Name, entries)
                    |> Map.ofSeq

                { defaultRawConfig with
                    RawOverrides = overrides }

let resolveConfig (raw: RawConfig) : Config =
    let resolved =
        raw.RawOverrides
        |> Map.toList
        |> List.choose (fun (name, overrides) ->
            let platformMatch =
                overrides |> List.tryFind (fun o -> o.Platform = Some Platform.current)

            let allMatch = overrides |> List.tryFind (fun o -> o.Platform = None)

            match platformMatch, allMatch with
            | Some m, _ -> Some(name, m)
            | None, Some m -> Some(name, m)
            | None, None -> None)
        |> Map.ofList

    { DefaultLine = raw.DefaultLine
      DefaultBranch = raw.DefaultBranch
      Overrides = resolved }

let loadConfig (path: string) : Config = loadRawConfig path |> resolveConfig

let private overrideToDict (ovr: Override) =
    let entry = System.Collections.Generic.Dictionary<string, obj>()
    entry.["line"] <- ovr.Line
    entry.["branch"] <- ovr.Branch

    match ovr.Reason with
    | Some r -> entry.["reason"] <- r
    | None -> ()

    match ovr.Platform with
    | Some p -> entry.["platform"] <- Platform.toString p
    | None -> ()

    entry

let saveRawConfig (path: string) (config: RawConfig) : unit =
    let dict = System.Collections.Generic.Dictionary<string, obj>()

    let overridesDict = System.Collections.Generic.Dictionary<string, obj>()

    for kv in config.RawOverrides do
        match kv.Value with
        | [ single ] when single.Platform = None -> overridesDict.[kv.Key] <- overrideToDict single
        | entries ->
            let arr = entries |> List.map overrideToDict |> List.toArray

            overridesDict.[kv.Key] <- arr

    dict.["overrides"] <- overridesDict

    let json = JsonSerializer.Serialize(dict, jsonOptions)
    File.WriteAllText(path, json)

let saveConfig (path: string) (config: Config) : unit =
    let raw =
        { DefaultLine = config.DefaultLine
          DefaultBranch = config.DefaultBranch
          RawOverrides = config.Overrides |> Map.map (fun _ ovr -> [ ovr ]) }

    saveRawConfig path raw

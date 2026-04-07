module CoverageRatchet.Thresholds

open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open CoverageRatchet.Cobertura

type Override =
    { Line: float
      Branch: float
      Reason: string
      Platform: string option }

let currentPlatform =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        "macos"
    elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
        "linux"
    elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        "windows"
    else
        "unknown"

type Config =
    { DefaultLine: float
      DefaultBranch: float
      Overrides: Map<string, Override> }

type RawConfig =
    { DefaultLine: float
      DefaultBranch: float
      RawOverrides: Map<string, Override list> }

type FileResult =
    { File: FileCoverage
      LineThreshold: float
      BranchThreshold: float
      LinePassed: bool
      BranchPassed: bool }

type CheckResult =
    | AllPassed
    | SomeFailed of FileResult list

let private defaultConfig =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      Overrides = Map.empty }

let private jsonOptions =
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
          BranchThreshold = branchThreshold
          LinePassed = f.LinePct >= lineThreshold
          BranchPassed = f.BranchPct >= branchThreshold })

let check (config: Config) (files: FileCoverage list) : CheckResult =
    let results = buildFileResults config files

    let failed =
        results |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)

    if List.isEmpty failed then AllPassed else SomeFailed failed

let private defaultRawConfig =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      RawOverrides = Map.empty }

let private parseOverrideElement (el: JsonElement) : Override =
    let line =
        if el.TryGetProperty("line") |> fst then
            el.GetProperty("line").GetDouble()
        else
            100.0

    let branch =
        if el.TryGetProperty("branch") |> fst then
            el.GetProperty("branch").GetDouble()
        else
            100.0

    let reason =
        match el.TryGetProperty("reason") with
        | true, r -> r.GetString()
        | false, _ -> ""

    let platform =
        match el.TryGetProperty("platform") with
        | true, p ->
            let s = p.GetString()
            if isNull s then None else Some s
        | false, _ -> None

    { Line = line
      Branch = branch
      Reason = if isNull reason then "" else reason
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
                overrides |> List.tryFind (fun o -> o.Platform = Some currentPlatform)

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
    entry.["reason"] <- ovr.Reason

    match ovr.Platform with
    | Some p -> entry.["platform"] <- p
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

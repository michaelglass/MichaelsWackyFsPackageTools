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

let loadConfig (path: string) : Config =
    if not (File.Exists(path)) then
        defaultConfig
    else
        let json = File.ReadAllText(path).Trim()

        if json = "{}" || System.String.IsNullOrWhiteSpace(json) then
            defaultConfig
        else
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            match root.TryGetProperty("overrides") with
            | false, _ -> defaultConfig
            | true, overridesEl ->
                let overrides =
                    overridesEl.EnumerateObject()
                    |> Seq.map (fun prop ->
                        let el = prop.Value

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

                        prop.Name,
                        { Line = line
                          Branch = branch
                          Reason = if isNull reason then "" else reason
                          Platform = platform })
                    |> Map.ofSeq

                if Map.isEmpty overrides then
                    defaultConfig
                else
                    { defaultConfig with
                        Overrides = overrides }

let saveConfig (path: string) (config: Config) : unit =
    let dict = System.Collections.Generic.Dictionary<string, obj>()

    let overridesDict = System.Collections.Generic.Dictionary<string, obj>()

    for kv in config.Overrides do
        let entry = System.Collections.Generic.Dictionary<string, obj>()
        entry.["line"] <- kv.Value.Line
        entry.["branch"] <- kv.Value.Branch
        entry.["reason"] <- kv.Value.Reason

        match kv.Value.Platform with
        | Some p -> entry.["platform"] <- p
        | None -> ()

        overridesDict.[kv.Key] <- entry

    dict.["overrides"] <- overridesDict

    let json = JsonSerializer.Serialize(dict, jsonOptions)
    File.WriteAllText(path, json)

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

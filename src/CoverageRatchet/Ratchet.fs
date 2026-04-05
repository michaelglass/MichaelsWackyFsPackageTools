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
            | None ->
                // File not in coverage data, keep override as-is
                Some(name, ovr)
            | Some file ->
                // Bump thresholds up if actual coverage exceeds them (never lower)
                let newLine = max ovr.Line file.LinePct
                let newBranch = max ovr.Branch file.BranchPct

                // If file now meets defaults, remove the override
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
    // Check for files below their thresholds
    let failedFiles =
        files
        |> List.filter (fun file ->
            let lineThreshold, branchThreshold =
                match Map.tryFind file.FileName config.Overrides with
                | Some ovr -> ovr.Line, ovr.Branch
                | None -> config.DefaultLine, config.DefaultBranch

            file.LinePct < lineThreshold || file.BranchPct < branchThreshold)
        |> List.map (fun f -> f.FileName)

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
            | None ->
                // File not in coverage data, keep override as-is
                Some(name, ovr)
            | Some file ->
                // If file now at 100%, remove the override
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
                          Reason = "loosened automatically" }
                        acc)
            updatedOverrides

    { config with Overrides = newOverrides }

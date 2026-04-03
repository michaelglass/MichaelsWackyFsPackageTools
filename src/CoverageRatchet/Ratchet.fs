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

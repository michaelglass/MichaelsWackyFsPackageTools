module CoverageRatchet.Program

open System
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

let private defaultConfigPath = "coverage-ratchet.json"

let private parseArgs (argv: string array) =
    let mutable command = None
    let mutable configPath = defaultConfigPath
    let mutable i = 0

    while i < argv.Length do
        match argv.[i] with
        | "check" -> command <- Some "check"
        | "ratchet" -> command <- Some "ratchet"
        | "--config" when i + 1 < argv.Length ->
            configPath <- argv.[i + 1]
            i <- i + 1
        | _ -> ()

        i <- i + 1

    command, configPath

let private formatFileResult (r: FileResult) =
    let branchStr =
        if r.File.BranchesTotal > 0 then
            sprintf " (%d/%d branches)" r.File.BranchesCovered r.File.BranchesTotal
        else
            ""

    let status = if r.LinePassed && r.BranchPassed then "PASS" else "FAIL"

    let thresholdStr =
        if r.LineThreshold < 100.0 || r.BranchThreshold < 100.0 then
            sprintf " [min: line=%.0f%% branch=%.0f%%]" r.LineThreshold r.BranchThreshold
        else
            ""

    sprintf
        "  %s %s: line=%.0f%% branch=%.0f%%%s%s"
        status
        r.File.FileName
        r.File.LinePct
        r.File.BranchPct
        branchStr
        thresholdStr

let private runCheck (configPath: string) =
    let config = loadConfig configPath

    match findCoverageFile "." with
    | None ->
        eprintfn "Error: No coverage.cobertura.xml found"
        1
    | Some xmlPath ->
        let files = parseFile xmlPath

        if List.isEmpty files then
            printfn "No F# source files found in coverage report."
            0
        else
            let allResults =
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

            let failed =
                allResults |> List.filter (fun r -> not r.LinePassed || not r.BranchPassed)

            let passed = allResults |> List.filter (fun r -> r.LinePassed && r.BranchPassed)

            if not (List.isEmpty failed) then
                printfn "FAILED files:"

                for r in failed do
                    printfn "%s" (formatFileResult r)

            if not (List.isEmpty passed) then
                printfn "Passed files:"

                for r in passed do
                    printfn "%s" (formatFileResult r)

            printfn ""

            printfn "Result: %d/%d files passed" passed.Length allResults.Length

            if List.isEmpty failed then 0 else 1

let private runRatchet (configPath: string) =
    let config = loadConfig configPath

    match findCoverageFile "." with
    | None ->
        eprintfn "Error: No coverage.cobertura.xml found"
        1
    | Some xmlPath ->
        let files = parseFile xmlPath
        let newConfig = ratchet config files
        saveConfig configPath newConfig

        let removed = config.Overrides.Count - newConfig.Overrides.Count

        let tightened =
            newConfig.Overrides
            |> Map.toList
            |> List.filter (fun (name, ovr) ->
                match Map.tryFind name config.Overrides with
                | Some old -> old.Line <> ovr.Line || old.Branch <> ovr.Branch
                | None -> false)
            |> List.length

        printfn "Ratchet complete: %d overrides tightened, %d removed" tightened removed
        0

[<EntryPoint>]
let main argv =
    match parseArgs argv with
    | Some "check", configPath -> runCheck configPath
    | Some "ratchet", configPath -> runRatchet configPath
    | _ ->
        printfn "Usage: coverageratchet <check|ratchet> [--config path]"
        1

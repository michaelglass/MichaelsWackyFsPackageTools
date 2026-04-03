module CoverageRatchet.Program

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

let defaultConfigPath = "coverage-ratchet.json"

let parseArgs (argv: string array) =
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

let formatFileResult (r: FileResult) =
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

let private runCheck (configPath: string) (files: FileCoverage list) =
    let config = loadConfig configPath

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

let private runRatchet (configPath: string) (files: FileCoverage list) =
    let config = loadConfig configPath
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

let run (command: string) (configPath: string) (searchDir: string) : Result<int, string> =
    match findCoverageFile searchDir with
    | None -> Error "No coverage.cobertura.xml found"
    | Some xmlPath ->
        let files = parseFile xmlPath

        match command with
        | "check" -> Ok(runCheck configPath files)
        | "ratchet" -> Ok(runRatchet configPath files)
        | other -> Error(sprintf "Unknown command: %s" other)

[<EntryPoint>]
let main argv =
    match parseArgs argv with
    | Some cmd, configPath ->
        match run cmd configPath "." with
        | Ok exitCode -> exitCode
        | Error msg ->
            eprintfn "Error: %s" msg
            1
    | None, _ ->
        printfn "Usage: coverageratchet <check|ratchet> [--config path]"
        1

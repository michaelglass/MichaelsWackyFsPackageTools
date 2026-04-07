module CoverageRatchet.Program

open CommandTree
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

let defaultConfigPath = "coverage-ratchet.json"

type Command =
    | [<Cmd("Tighten thresholds to match current coverage (default)"); CmdDefault>] Ratchet of config: string option
    | [<Cmd("Check coverage against thresholds")>] Check of config: string option
    | [<Cmd("Set thresholds to current coverage (makes check pass immediately)")>] Loosen of config: string option

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
        let allResults = buildFileResults config files

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
    let raw = loadRawConfig configPath
    let config = resolveConfig raw

    match ratchetRawWithStatus raw files with
    | NoChanges _ ->
        printfn "Ratchet complete: no changes needed"
        0
    | Tightened newRaw ->
        saveRawConfig configPath newRaw
        let newConfig = resolveConfig newRaw

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
        1
    | Failed(newRaw, failedFiles) ->
        saveRawConfig configPath newRaw
        eprintfn "Coverage below threshold for: %s" (String.concat ", " failedFiles)
        2

let private runLoosen (configPath: string) (files: FileCoverage list) =
    let raw = loadRawConfig configPath
    let newRaw = loosenRaw raw files
    saveRawConfig configPath newRaw
    printfn "Loosen complete: thresholds set to current coverage"
    0

let run (command: Command) (searchDir: string) : Result<int, string> =
    let configPath =
        match command with
        | Ratchet(config = c)
        | Check(config = c)
        | Loosen(config = c) -> c |> Option.defaultValue defaultConfigPath

    match findCoverageFile searchDir with
    | None -> Error "No coverage.cobertura.xml found"
    | Some xmlPath ->
        let files = parseFile xmlPath

        match command with
        | Ratchet _ -> Ok(runRatchet configPath files)
        | Check _ -> Ok(runCheck configPath files)
        | Loosen _ -> Ok(runLoosen configPath files)

[<EntryPoint>]
let main argv =
    let tree =
        CommandReflection.fromUnion<Command> "Per-file coverage enforcement that only goes up"

    // Bare invocation runs the default (ratchet) command
    if Array.isEmpty argv then
        match run (Ratchet None) "." with
        | Ok exitCode -> exitCode
        | Error msg ->
            eprintfn "Error: %s" msg
            1
    elif argv |> Array.exists (fun a -> a = "--help" || a = "-h" || a = "help") then
        printfn "%s" (CommandTree.helpFull tree "coverageratchet")
        0
    else
        match CommandTree.parse tree argv with
        | Ok cmd ->
            match run cmd "." with
            | Ok exitCode -> exitCode
            | Error msg ->
                eprintfn "Error: %s" msg
                1
        | Error(HelpRequested path) ->
            printfn "%s" (CommandTree.helpForPath tree path "coverageratchet")
            0
        | Error err ->
            eprintfn "Error: %A" err
            1

module CoverageRatchet.Program

open System.IO
open System.Text.Json
open System.Threading
open CommandTree
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Shell

let defaultConfigPath = "coverage-ratchet.json"

type Command =
    | [<Cmd("Tighten thresholds to match current coverage (default)"); CmdDefault>] Ratchet of config: string option
    | [<Cmd("Check coverage against thresholds")>] Check of config: string option
    | [<Cmd("Set thresholds to current coverage (makes check pass immediately)")>] Loosen of config: string option
    | [<Cmd("Output coverage results as JSON for CI artifact upload")>] CheckJson of
        config: string option *
        output: string option
    | [<Cmd("Fetch CI coverage results and update local platform-specific thresholds")>] LoosenFromCi of
        config: string option

let formatFileResult (r: FileResult) =
    let branchStr =
        if r.File.BranchesTotal > 0 then
            sprintf " (%d/%d branches)" r.File.BranchesCovered r.File.BranchesTotal
        else
            ""

    let status = if FileResult.passed r then "PASS" else "FAIL"

    let thresholdStr =
        if r.LineThreshold < 100.0 || r.BranchThreshold < 100.0 then
            sprintf " [min: line=%.1f%% branch=%.1f%%]" r.LineThreshold r.BranchThreshold
        else
            ""

    sprintf
        "  %s %s: line=%.1f%% branch=%.1f%%%s%s"
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

        let failed, passed =
            allResults |> List.partition (fun r -> not (FileResult.passed r))

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
    | NoChanges ->
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

let private runCheckJson (configPath: string) (outputPath: string) (files: FileCoverage list) =
    let config = loadConfig configPath
    let allResults = buildFileResults config files

    let resultsDict = System.Collections.Generic.Dictionary<string, obj>()

    for r in allResults do
        let entry = System.Collections.Generic.Dictionary<string, obj>()
        entry.["line"] <- int (floor r.File.LinePct)
        entry.["branch"] <- int (floor r.File.BranchPct)
        resultsDict.[r.File.FileName] <- entry

    let wrapper = System.Collections.Generic.Dictionary<string, obj>()
    wrapper.["platform"] <- Platform.toString Platform.current
    wrapper.["results"] <- resultsDict

    let json = JsonSerializer.Serialize(wrapper, jsonOptions)
    File.WriteAllText(outputPath, json)

    let failed = allResults |> List.filter (fun r -> not (FileResult.passed r))
    if List.isEmpty failed then 0 else 1

type CiResult =
    | CiPassed
    | CiOtherFailure
    | CiCoverageFailure of artifactDir: string

let internal resolveGitDir (repoRoot: string) : string option =
    let dotGit = Path.Combine(repoRoot, ".git")

    if Directory.Exists(dotGit) || File.Exists(dotGit) then
        None
    else
        let jjGitDir = Path.Combine(repoRoot, ".jj", "repo", "store", "git")

        if Directory.Exists(jjGitDir) then
            Some(Path.GetFullPath(jjGitDir))
        else
            None

let private withJjGitDir (f: unit -> 'a) : 'a =
    let gitDir = resolveGitDir (Directory.GetCurrentDirectory())

    match gitDir with
    | Some dir -> System.Environment.SetEnvironmentVariable("GIT_DIR", dir)
    | None -> ()

    try
        f ()
    finally
        match gitDir with
        | Some _ -> System.Environment.SetEnvironmentVariable("GIT_DIR", null)
        | None -> ()

let internal pollCi
    (run: string -> string -> CommandResult)
    (sha: string)
    (intervalMs: int)
    (maxAttempts: int)
    : CiResult =
    let rec poll attempt =
        if attempt > maxAttempts then
            eprintfn "CI polling timed out after %d attempts" maxAttempts
            CiOtherFailure
        else
            let result =
                withJjGitDir (fun () ->
                    run "gh" (sprintf "run list --commit %s --json status,conclusion,databaseId" sha))

            match result with
            | Failure(msg, _) ->
                eprintfn "gh run list failed: %s" msg
                CiOtherFailure
            | Success output ->
                use doc = JsonDocument.Parse(output)
                let runs = doc.RootElement.EnumerateArray() |> Seq.toList

                if List.isEmpty runs then
                    if attempt < maxAttempts then
                        printfn "No CI runs found yet, waiting..."
                        Thread.Sleep(intervalMs)
                        poll (attempt + 1)
                    else
                        eprintfn "No CI runs found for %s" sha
                        CiOtherFailure
                else
                    let allCompleted =
                        runs |> List.forall (fun r -> r.GetProperty("status").GetString() = "completed")

                    if not allCompleted then
                        printfn "CI still running, waiting..."
                        Thread.Sleep(intervalMs)
                        poll (attempt + 1)
                    else
                        let allPassed =
                            runs
                            |> List.forall (fun r -> r.GetProperty("conclusion").GetString() = "success")

                        if allPassed then
                            CiPassed
                        else
                            let failedRun =
                                runs
                                |> List.tryFind (fun r -> r.GetProperty("conclusion").GetString() <> "success")

                            match failedRun with
                            | None -> CiPassed
                            | Some r ->
                                let runId = r.GetProperty("databaseId").GetInt64()

                                let tmpDir = Path.Combine(Path.GetTempPath(), sprintf "coverage-%d" runId)

                                let dlResult =
                                    withJjGitDir (fun () ->
                                        run "gh" (sprintf "run download %d -n coverage-thresholds -D %s" runId tmpDir))

                                match dlResult with
                                | Success _ -> CiCoverageFailure tmpDir
                                | Failure _ -> CiOtherFailure

    poll 1

let internal getVcsSha (run: string -> string -> CommandResult) : string =
    match run "jj" "log -r @- --no-graph -T commit_id" with
    | Success sha -> sha.Trim()
    | Failure _ ->
        match run "git" "rev-parse HEAD" with
        | Success sha -> sha.Trim()
        | Failure(msg, _) -> failwithf "Could not get commit SHA: %s" msg

let internal vcsPush (run: string -> string -> CommandResult) =
    match run "jj" "git push" with
    | Success _ -> ()
    | Failure _ ->
        match run "git" "push" with
        | Success _ -> ()
        | Failure(msg, _) -> failwithf "git push failed: %s" msg

let internal vcsCommitAndPush (run: string -> string -> CommandResult) (configPath: string) =
    let mustRun cmd args =
        match run cmd args with
        | Success _ -> ()
        | Failure(msg, _) -> failwithf "%s %s failed: %s" cmd args msg

    match run "jj" "describe -m \"fix: update coverage thresholds from CI\"" with
    | Success _ ->
        mustRun "jj" "bookmark set main -r @"
        mustRun "jj" "new"
        mustRun "jj" "git push --bookmark main"
    | Failure _ ->
        mustRun "git" (sprintf "add %s" configPath)
        mustRun "git" "commit -m \"fix: update coverage thresholds from CI\""
        mustRun "git" "push"

let private runLoosenFromCi (configPath: string) : int =
    vcsPush run
    let sha = getVcsSha run
    printfn "Polling CI for commit %s..." sha

    match pollCi run sha 30000 60 with
    | CiPassed ->
        printfn "CI passed, no coverage loosening needed."
        0
    | CiOtherFailure ->
        eprintfn "CI failed for non-coverage reasons."
        1
    | CiCoverageFailure artifactDir ->
        printfn "CI coverage failure detected, downloading thresholds..."

        try
            let thresholdFiles = Directory.GetFiles(artifactDir, "coverage-thresholds-*.json")

            for thresholdFile in thresholdFiles do
                let json = File.ReadAllText(thresholdFile)
                let ciPlatform, ciResults = parseCiThresholds json

                let projectName =
                    Path.GetFileNameWithoutExtension(thresholdFile).Replace("coverage-thresholds-", "")

                let localConfigPath =
                    if projectName = "" || projectName = "default" then
                        configPath
                    else
                        sprintf "coverage-ratchet-%s.json" projectName

                let raw = loadRawConfig localConfigPath
                let merged = mergeFromCi raw ciPlatform ciResults
                saveRawConfig localConfigPath merged
                printfn "Updated %s with %s thresholds" localConfigPath (Platform.toString ciPlatform)
        finally
            try
                Directory.Delete(artifactDir, true)
            with _ ->
                ()

        vcsCommitAndPush run configPath
        let newSha = getVcsSha run
        printfn "Re-polling CI for commit %s..." newSha

        match pollCi run newSha 30000 60 with
        | CiPassed ->
            printfn "CI passed after threshold update."
            0
        | _ ->
            eprintfn "CI still failing after threshold update."
            1

type CoverageFileCommand =
    | CfRatchet
    | CfCheck
    | CfLoosen
    | CfCheckJson of output: string option

let private runWithCoverageFile (cmd: CoverageFileCommand) (configPath: string) (files: FileCoverage list) =
    match cmd with
    | CfRatchet -> runRatchet configPath files
    | CfCheck -> runCheck configPath files
    | CfLoosen -> runLoosen configPath files
    | CfCheckJson outputOpt ->
        let outputPath = outputOpt |> Option.defaultValue "coverage-results.json"
        runCheckJson configPath outputPath files

let run (command: Command) (searchDir: string) : Result<int, string> =
    let configPath =
        match command with
        | Ratchet(config = c)
        | Check(config = c)
        | Loosen(config = c)
        | CheckJson(config = c)
        | LoosenFromCi(config = c) -> c |> Option.defaultValue defaultConfigPath

    let coverageFileCmd =
        match command with
        | Ratchet _ -> Some CfRatchet
        | Check _ -> Some CfCheck
        | Loosen _ -> Some CfLoosen
        | CheckJson(output = outputOpt) -> Some(CfCheckJson outputOpt)
        | LoosenFromCi _ -> None

    match coverageFileCmd with
    | None -> Ok(runLoosenFromCi configPath)
    | Some cmd ->
        match findCoverageFile searchDir with
        | None -> Error "No coverage.cobertura.xml found"
        | Some xmlPath ->
            let files = parseFile xmlPath
            Ok(runWithCoverageFile cmd configPath files)

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

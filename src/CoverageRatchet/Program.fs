module CoverageRatchet.Program

open System.IO
open System.Text.Json
open System.Threading
open CommandTree
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Shell
open CoverageRatchet.Merge

let defaultConfigPath = "coverage-ratchet.json"

type Command =
    | [<Cmd("Tighten thresholds to match current coverage (default)"); CmdDefault>] Ratchet of config: string option
    | [<Cmd("Check coverage against thresholds (exits non-zero if any file fails)")>] Check of config: string option
    | [<Cmd("Set thresholds down to current coverage (makes check pass immediately)")>] Loosen of config: string option
    | [<Cmd("Write per-file coverage results as JSON for CI artifact upload")>] CheckJson of
        config: string option *
        output: string option
    | [<Cmd("List files sorted by coverage to find improvement targets")>] Targets of config: string option
    | [<Cmd("List uncovered branch points per file")>] Gaps of config: string option
    | [<Cmd("Pull CI coverage results into local platform-specific thresholds")>] LoosenFromCi of config: string option
    | [<Cmd("Merge two Cobertura files by taking max hits per line")>] Merge of
        baseline: string *
        partial: string *
        output: string
    | [<Cmd("Copy each coverage.cobertura.xml to coverage.baseline.xml in the search dir")>] RefreshBaseline

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

let private runTargets (configPath: string) (files: FileCoverage list) =
    let config = loadConfig configPath
    let allResults = buildFileResults config files

    let sorted = allResults |> List.sortBy (fun r -> r.File.LinePct)

    printfn ""
    printfn "Files by coverage (lowest first):"
    printfn "────────────────────────────────────────────────────────────────"
    printfn "%8s %8s  %s" "Line Cov" "Br. Cov" "File"
    printfn "────────────────────────────────────────────────────────────────"

    for r in sorted do
        printfn "  %5.1f%%  %5.1f%%  %s" r.File.LinePct r.File.BranchPct r.File.FileName

    printfn "────────────────────────────────────────────────────────────────"
    printfn ""
    printfn "  %d files" sorted.Length
    printfn ""
    0

let private runGaps (xmlContents: string list) =
    let rawLines = xmlContents |> List.collect extractRawLines
    let gapFiles = buildBranchGaps rawLines

    if List.isEmpty gapFiles then
        printfn "No uncovered branches found."
    else
        for file in gapFiles do
            printfn "  %-45s %.1f%% branch (%d uncovered)" file.FileName file.BranchPct file.Gaps.Length

            for gap in file.Gaps do
                printfn "      line %d: %d/%d branches covered" gap.Line gap.Covered gap.Total

        printfn ""

        let totalGaps = gapFiles |> List.sumBy (fun f -> f.Gaps.Length)
        printfn "  Summary: %d uncovered branches across %d files" totalGaps gapFiles.Length

    printfn ""
    0

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

let internal runLoosenFromCi (runShell: string -> string -> CommandResult) (configPath: string) : int =
    vcsPush runShell
    let sha = getVcsSha runShell
    printfn "Polling CI for commit %s..." sha

    match pollCi runShell sha 30000 60 with
    | CiPassed ->
        printfn "CI passed, no coverage loosening needed."
        0
    | CiOtherFailure ->
        eprintfn "CI failed for non-coverage reasons."
        1
    | CiCoverageFailure artifactDir ->
        printfn "CI coverage failure detected, downloading thresholds..."

        let parseFile thresholdFile =
            try
                let json = File.ReadAllText(thresholdFile)
                let ciPlatform, ciResults = parseCiThresholds json

                let projectName =
                    Path.GetFileNameWithoutExtension(thresholdFile).Replace("coverage-thresholds-", "")

                let localConfigPath =
                    if projectName = "" || projectName = "default" then
                        configPath
                    else
                        sprintf "coverage-ratchet-%s.json" projectName

                Ok(localConfigPath, ciPlatform, ciResults)
            with ex ->
                eprintfn "Failed to parse %s: %s" thresholdFile ex.Message
                Error thresholdFile

        let writeConfig (localConfigPath: string) (platformResults: (Platform * Map<string, CiFileResult>) list) =
            try
                let raw = loadRawConfig localConfigPath

                let merged =
                    platformResults
                    |> List.fold (fun acc (plat, res) -> mergeFromCi acc plat res) raw

                saveRawConfig localConfigPath merged

                let platforms =
                    platformResults |> List.map (fst >> Platform.toString) |> String.concat ", "

                printfn "Updated %s with %s thresholds" localConfigPath platforms
                Ok localConfigPath
            with ex ->
                eprintfn "Failed to save %s: %s" localConfigPath ex.Message
                Error localConfigPath

        let writeResults, parseFailures =
            try
                let thresholdFiles = Directory.GetFiles(artifactDir, "coverage-thresholds-*.json")

                if Array.isEmpty thresholdFiles then
                    eprintfn
                        "No coverage-thresholds-*.json files found in CI artifact at %s. \
                         The CI workflow must upload a 'coverage-thresholds' artifact containing \
                         per-project threshold files (e.g. coverage-thresholds-MyProj.json)."
                        artifactDir

                let parsed = thresholdFiles |> Array.map parseFile |> Array.toList

                let parseFailures =
                    parsed
                    |> List.sumBy (function
                        | Error _ -> 1
                        | Ok _ -> 0)

                let writes =
                    parsed
                    |> List.choose (function
                        | Ok x -> Some x
                        | Error _ -> None)
                    |> List.groupBy (fun (path, _, _) -> path)
                    |> List.map (fun (path, entries) ->
                        let platformResults = entries |> List.map (fun (_, plat, res) -> plat, res)
                        writeConfig path platformResults)

                writes, parseFailures
            finally
                try
                    Directory.Delete(artifactDir, true)
                with
                | :? IOException
                | :? System.UnauthorizedAccessException as ex ->
                    eprintfn "Warning: could not clean up artifact directory %s: %s" artifactDir ex.Message

        let updates =
            writeResults
            |> List.sumBy (function
                | Ok _ -> 1
                | Error _ -> 0)

        let writeFailures =
            writeResults
            |> List.sumBy (function
                | Error _ -> 1
                | Ok _ -> 0)

        let failures = parseFailures + writeFailures

        if updates = 0 || failures > 0 then
            eprintfn
                "loosen-from-ci did not update any thresholds (%d updates, %d failures). Not committing."
                updates
                failures

            1
        else
            vcsCommitAndPush runShell configPath
            let newSha = getVcsSha runShell
            printfn "Re-polling CI for commit %s..." newSha

            match pollCi runShell newSha 30000 60 with
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
    | CfTargets
    | CfGaps

let private runWithCoverageFiles
    (cmd: CoverageFileCommand)
    (configPath: string)
    (xmlPaths: string list)
    (files: FileCoverage list)
    =
    match cmd with
    | CfRatchet -> runRatchet configPath files
    | CfCheck -> runCheck configPath files
    | CfLoosen -> runLoosen configPath files
    | CfCheckJson outputOpt ->
        let outputPath = outputOpt |> Option.defaultValue "coverage-results.json"
        runCheckJson configPath outputPath files
    | CfTargets -> runTargets configPath files
    | CfGaps -> runGaps (xmlPaths |> List.map File.ReadAllText)

let run (command: Command) (searchDir: string) (mergeBaselines: bool) : Result<int, string> =
    match command with
    | Merge(baseline, partialFile, output) ->
        Merge.mergeFiles baseline partialFile output
        printfn "merged %s + %s -> %s" baseline partialFile output
        Ok 0
    | RefreshBaseline ->
        Merge.refreshBaselines searchDir
        Ok 0
    | _ ->
        let configPath =
            match command with
            | Ratchet(config = c)
            | Check(config = c)
            | Loosen(config = c)
            | CheckJson(config = c)
            | Targets(config = c)
            | Gaps(config = c)
            | LoosenFromCi(config = c) -> c |> Option.defaultValue defaultConfigPath
            | Merge _
            | RefreshBaseline -> defaultConfigPath

        let coverageFileCmd =
            match command with
            | Ratchet _ -> Some CfRatchet
            | Check _ -> Some CfCheck
            | Loosen _ -> Some CfLoosen
            | CheckJson(output = outputOpt) -> Some(CfCheckJson outputOpt)
            | Targets _ -> Some CfTargets
            | Gaps _ -> Some CfGaps
            | LoosenFromCi _ -> None
            | Merge _
            | RefreshBaseline -> None

        match coverageFileCmd with
        | None -> Ok(runLoosenFromCi Shell.run configPath)
        | Some cmd ->
            // Layer each coverage.cobertura.xml onto a per-project baseline
            // before reading, so impact-filtered partial runs can't lower the
            // ratchet. Skipped unless --merge-baselines is set.
            if mergeBaselines then
                Merge.mergeIntoBaselines searchDir

            let xmlPaths = findCoverageFiles searchDir

            if List.isEmpty xmlPaths then
                Error "No coverage.cobertura.xml found"
            else
                let files = parseFiles xmlPaths
                let result = runWithCoverageFiles cmd configPath xmlPaths files

                // If the run just completed a known-full test suite (signalled
                // by fs-hot-watch via FSHW_RAN_FULL_SUITE=true), advance the
                // baseline to the current coverage so stale hits from deleted
                // tests drop out.
                if
                    mergeBaselines
                    && result = 0
                    && System.Environment.GetEnvironmentVariable("FSHW_RAN_FULL_SUITE") = "true"
                then
                    Merge.refreshBaselines searchDir

                Ok result

let extractFlags (argv: string array) : string * bool * string array =
    let rec loop i searchDir mergeBaselines remaining =
        if i >= argv.Length then
            searchDir, mergeBaselines, Array.ofList (List.rev remaining)
        elif argv.[i] = "--search-dir" && i + 1 < argv.Length then
            loop (i + 2) argv.[i + 1] mergeBaselines remaining
        elif argv.[i] = "--merge-baselines" then
            loop (i + 1) searchDir true remaining
        else
            loop (i + 1) searchDir mergeBaselines (argv.[i] :: remaining)

    loop 0 "." false []

let private subcommandExtras (path: string list) : string option =
    match path with
    | [ "ratchet" ] ->
        Some
            """
For each F# file under --search-dir, raise [config]'s line+branch
threshold to the current coverage. Coverage can only go up. Files
not listed in [config] must hit 100%/100% (which is also the default
for newly-encountered files).

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "check" ] ->
        Some
            """
Exits 0 if every F# file meets its line+branch threshold, 1 otherwise.
Use in CI. Files not listed in [config] must hit 100%/100%.

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "loosen" ] ->
        Some
            """
Lower thresholds in [config] to match current coverage so 'check'
passes. Use sparingly — bootstrapping, or after a deliberate drop.
Unlike 'ratchet' this can move thresholds DOWN.

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "check-json" ] ->
        Some
            """
Run 'check' and write per-file results as JSON for CI to upload as
an artifact. Output includes the detected platform so loosen-from-ci
can merge results from other platforms back in.

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
  output  path to write JSON results (default: coverage-results.json)
"""
    | [ "targets" ] ->
        Some
            """
Lists every F# file with line and branch percentages, lowest first.
Read-only; never modifies [config]. Use to find what to test next.

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "gaps" ] ->
        Some
            """
For each file with branches < 100%, prints the source lines where
uncovered branches sit. Use to plan tests for partial branch
coverage.

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "loosen-from-ci" ] ->
        Some
            """
Push the current commit, poll GitHub Actions, and if the failure was
a coverage shortfall on a platform other than yours, download the
'coverage-thresholds' CI artifact and merge those numbers into
[config] as platform-tagged overrides. Then commits and pushes.

Requires:
  - gh CLI authenticated to the repo
  - CI workflow that uploads a 'coverage-thresholds' artifact built
    by 'check-json' (one coverage-thresholds-<project>.json per project)

Arguments:
  config  path to the JSON config file (default: coverage-ratchet.json)
"""
    | [ "merge" ] ->
        Some
            """
Layers the two Cobertura files line-by-line, keeping the higher hit
count for each line, and writes the result to <output>. Use this when
a partial test run (e.g. impact-filtered) would otherwise drop coverage
below the ratchet for lines whose tests didn't re-run.

Most users want --merge-baselines on check/ratchet instead, which
does this automatically per project under --search-dir using a
coverage.baseline.xml sibling file.

Arguments:
  baseline  Cobertura XML from the prior full run
  partial   Cobertura XML from the current (possibly partial) run
  output    where to write the merged Cobertura XML

Example:
  coverageratchet merge \\
    coverage/MyProj/coverage.baseline.xml \\
    coverage/MyProj/coverage.cobertura.xml \\
    coverage/MyProj/coverage.cobertura.xml
"""
    | [ "refresh-baseline" ] ->
        Some
            """
For every coverage.cobertura.xml under --search-dir, copy it to
coverage.baseline.xml beside it. Run this after a deliberate
full-suite test pass to advance the baseline so stale hits from
deleted tests drop out of subsequent merged runs.
"""
    | _ -> None

let private rootHelpExtras =
    """
Global flags (can appear anywhere):
  --search-dir <path>   directory to scan for coverage.cobertura.xml
                        (default: current directory; recursive)
  --merge-baselines     before reading coverage, merge each
                        coverage.cobertura.xml onto its sibling
                        coverage.baseline.xml (max hits per line) so
                        partial test runs cannot lower the ratchet.
                        Bootstraps a baseline on first use.

Config file format (default: coverage-ratchet.json):
  {
    "overrides": {
      "Program.fs": {
        "line": 85.5,
        "branch": 77.0,
        "reason": "CLI entry point — exit calls are not coverable"
      },
      "Os.fs": [
        { "line": 79, "branch": 76, "platform": "macos" },
        { "line": 46, "branch": 44, "platform": "linux" }
      ]
    }
  }

  - Files not listed must have 100% line and 100% branch coverage.
  - "platform" is optional: "macos" | "linux" | "windows". Use an array of
    entries when coverage differs per platform; a platform-less entry
    serves as fallback.
  - "reason" is free-form prose explaining the override.

Examples:
  coverageratchet                         # ratchet using ./coverage-ratchet.json
  coverageratchet --search-dir coverage check
  coverageratchet --search-dir coverage check --merge-baselines
  coverageratchet targets coverage-ratchet-MyProj.json
  coverageratchet merge prior.xml run.xml merged.xml

Run 'coverageratchet <command> --help' for command-specific details.
"""

let private normalizeHelpFlags (argv: string array) : string array =
    argv |> Array.map (fun a -> if a = "-h" || a = "help" then "--help" else a)

[<EntryPoint>]
let main argv =
    let argv = normalizeHelpFlags argv
    let searchDir, mergeBaselines, argv = extractFlags argv

    let tree =
        CommandReflection.fromUnion<Command> "Per-file coverage enforcement that only goes up"

    let printHelp (path: string list) =
        printfn "%s" (CommandTree.helpForPath tree path "coverageratchet")

        if List.isEmpty path then
            printfn "%s" rootHelpExtras
        else
            match subcommandExtras path with
            | Some extras -> printfn "%s" extras
            | None -> ()

    // Bare invocation runs the default (ratchet) command
    if Array.isEmpty argv then
        match run (Ratchet None) searchDir mergeBaselines with
        | Ok exitCode -> exitCode
        | Error msg ->
            eprintfn "Error: %s" msg
            1
    else
        match CommandTree.parse tree argv with
        | Ok cmd ->
            match run cmd searchDir mergeBaselines with
            | Ok exitCode -> exitCode
            | Error msg ->
                eprintfn "Error: %s" msg
                1
        | Error(HelpRequested path) ->
            printHelp path
            0
        | Error err ->
            eprintfn "Error: %A" err
            1

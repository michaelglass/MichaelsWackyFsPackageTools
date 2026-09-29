/// `propose-from-ci`: read a CI run's `coverage-thresholds` artifact and draft a
/// floor for every file that fell below the floor its platform enforces.
///
/// It is a proposal and nothing more. It reads floor files and never writes one,
/// and the only commands it runs are two read-only `gh` calls. A red CI run
/// used to reveal one project's shortfall per round trip, because the check stops
/// at the first failing project; this lists them all from one run, with the
/// evidence a reviewer needs to accept or refuse each floor.
module CoverageRatchet.ProposeFromCi

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Shell

/// The artifact `check-json` output is uploaded under by the reusable build workflow.
[<Literal>]
let ArtifactName = "coverage-thresholds"

/// `coverage-thresholds-<project>.json` → `<project>`.
let projectOfThresholdFile (path: string) : string =
    Path.GetFileNameWithoutExtension(path).Replace("coverage-thresholds-", "")

/// The floor file a project's thresholds belong to. `default` and the empty
/// project name map to `defaultConfigPath`.
let configPathForProject (defaultConfigPath: string) (project: string) : string =
    if project = "" || project = "default" then
        defaultConfigPath
    else
        sprintf "coverage-ratchet-%s.json" project

/// What the run says about itself; every drafted reason cites it.
type RunEvidence =
    {
        RunId: string
        HeadSha: string
        Conclusion: string
        WorkflowName: string
        Url: string
    }

/// Where the floor a file fell below came from.
type FloorSource =
    /// An entry tagged with the run's platform.
    | PlatformEntry
    /// A platform-less entry, which every platform without its own entry shares.
    | SharedEntry
    /// No entry for the file: the 100%/100% default.
    | DefaultFloor

/// One file below the floor its platform enforces, or possibly below it.
type Shortfall =
    {
        File: string
        Measured: CiFileResult
        FloorLine: float
        FloorBranch: float
        Source: FloorSource
        /// The reason on the platform entry the proposal replaces, when there is one.
        PreviousReason: string option
        /// False when the artifact's whole-number rounding hides whether the file fell:
        /// no entry is drafted for it.
        Determined: bool
        /// Every entry the file's key would hold if the proposal were applied; [] when
        /// the shortfall is not determined.
        Proposed: Override list
    }

/// One `coverage-thresholds-<project>.json` from the artifact, judged against its floor file.
type ProjectProposal =
    {
        Project: string
        ConfigPath: string
        ConfigFound: bool
        Platform: Platform
        FilesMeasured: int
        Shortfalls: Shortfall list
    }

let private pct (value: float) = sprintf "%g%%" value

let private shortSha (sha: string) =
    if sha.Length > 8 then sha.Substring(0, 8) else sha

/// The reason a drafted entry carries: which run, which commit, what it measured,
/// and against what. Numbers are the proposed floor's, which are rounded down.
let draftReason (evidence: RunEvidence) (platform: Platform) (s: Shortfall) (line: float) (branch: float) =
    sprintf
        "%s CI run %s (commit %s) measured line %s, branch %s against a floor of line %s, branch %s; \
         floor set to the measured value, rounded down."
        (Platform.toString platform)
        evidence.RunId
        (shortSha evidence.HeadSha)
        (pct line)
        (pct branch)
        (pct s.FloorLine)
        (pct s.FloorBranch)

/// PURE: the entries a file's key would hold. Only the floor for `platform` moves,
/// and only down, to the measured value rounded down: the dimension that held keeps
/// its floor. Entries for other platforms, and a platform-less entry, stay as they
/// are — the new entry is tagged, so it cannot lower any other platform's floor.
let private proposeEntries
    (evidence: RunEvidence)
    (platform: Platform)
    (existing: Override list)
    (s: Shortfall)
    : Override list =
    let line = min s.FloorLine (floor s.Measured.Line)
    let branch = min s.FloorBranch (floor s.Measured.Branch)

    let entry =
        {
            Line = line
            Branch = branch
            Reason = Some(draftReason evidence platform s line branch)
            Platform = Some platform
        }

    if existing |> List.exists (fun e -> e.Platform = Some platform) then
        existing |> List.map (fun e -> if e.Platform = Some platform then entry else e)
    else
        existing @ [ entry ]

/// `check-json` writes whole percentages, rounded down, so a measured 69 means
/// somewhere in [69, 70). Against a fractional floor such as 69.1 that value may or
/// may not have held; only a floor at or above the next whole number was certainly
/// missed. A fractional measurement is taken as exact.
let private certainlyBelow (measured: float) (floorValue: float) =
    if measured = floor measured then
        measured + 1.0 <= floorValue
    else
        measured < floorValue

/// PURE: every file in `results` below (or, through rounding, possibly below) the
/// floor `platform` enforces in `raw`, with drafted entries for the ones certainly
/// below. A file the artifact did not measure is not judged.
let findShortfalls
    (evidence: RunEvidence)
    (platform: Platform)
    (raw: RawConfig)
    (results: Map<string, CiFileResult>)
    : Shortfall list =
    let resolved = resolveConfigFor platform raw

    results
    |> Map.toList
    |> List.choose (fun (file, measured) ->
        let floorLine, floorBranch, source, previousReason =
            match Map.tryFind file resolved.Overrides with
            | Some o when o.Platform = Some platform -> o.Line, o.Branch, PlatformEntry, o.Reason
            | Some o -> o.Line, o.Branch, SharedEntry, None
            | None -> resolved.DefaultLine, resolved.DefaultBranch, DefaultFloor, None

        if measured.Line >= floorLine && measured.Branch >= floorBranch then
            None
        else
            let determined =
                certainlyBelow measured.Line floorLine
                || certainlyBelow measured.Branch floorBranch

            let shortfall =
                {
                    File = file
                    Measured = measured
                    FloorLine = floorLine
                    FloorBranch = floorBranch
                    Source = source
                    PreviousReason = previousReason
                    Determined = determined
                    Proposed = []
                }

            if determined then
                let existing = Map.tryFind file raw.RawOverrides |> Option.defaultValue []

                Some
                    { shortfall with
                        Proposed = proposeEntries evidence platform existing shortfall
                    }
            else
                Some shortfall)

/// Judge one artifact file against the floor file it belongs to. Reads the floor
/// file; never writes it.
let judgeThresholdFile
    (evidence: RunEvidence)
    (configDir: string)
    (defaultConfigPath: string)
    (thresholdFile: string)
    : ProjectProposal =
    let platform, results = parseCiThresholds (File.ReadAllText thresholdFile)
    let project = projectOfThresholdFile thresholdFile

    let configPath =
        Path.Combine(configDir, configPathForProject defaultConfigPath project)

    {
        Project = project
        ConfigPath = configPath
        ConfigFound = File.Exists configPath
        Platform = platform
        FilesMeasured = results.Count
        Shortfalls = findShortfalls evidence platform (loadRawConfig configPath) results
    }

let private describeSource (source: FloorSource) (platform: Platform) =
    match source with
    | PlatformEntry -> sprintf "the %s entry" (Platform.toString platform)
    | SharedEntry -> "the platform-less entry"
    | DefaultFloor -> "the default; the file has no entry"

let private renderEntry (p: ProjectProposal) (s: Shortfall) =
    if s.Determined then
        [
            sprintf "Proposed `overrides` entry in %s:" (Path.GetFileName p.ConfigPath)
            ""
            "```json"
            sprintf "\"%s\": %s" s.File (overrideEntriesToJson s.Proposed)
            "```"
        ]
    else
        [
            "UNDETERMINED: the artifact rounds down to whole percentages, so it cannot tell"
            "whether this file cleared its fractional floor. No entry drafted; measure it"
            "on that platform or re-run CI before changing this floor."
        ]

let private renderShortfall (p: ProjectProposal) (s: Shortfall) =
    [
        sprintf "### %s" s.File
        ""
        sprintf "- measured: line %s, branch %s" (pct s.Measured.Line) (pct s.Measured.Branch)
        sprintf
            "- %s floor: line %s, branch %s (%s)"
            (Platform.toString p.Platform)
            (pct s.FloorLine)
            (pct s.FloorBranch)
            (describeSource s.Source p.Platform)
        match s.PreviousReason with
        | Some r -> sprintf "- reason it replaces: %s" r
        | None -> ()
        ""
    ]
    @ renderEntry p s
    @ [ "" ]

let private renderProject (p: ProjectProposal) =
    let configName = Path.GetFileName p.ConfigPath

    let missing =
        if p.ConfigFound then
            []
        else
            [
                sprintf "%s was not found, so every file is judged against the 100%%/100%% default." configName
                ""
            ]

    let header =
        sprintf
            "## %s — %d of %d measured file(s) below the %s floor%s"
            configName
            (p.Shortfalls |> List.filter (fun s -> s.Determined) |> List.length)
            p.FilesMeasured
            (Platform.toString p.Platform)
            (match p.Shortfalls |> List.filter (fun s -> not s.Determined) |> List.length with
             | 0 -> ""
             | n -> sprintf ", %d undetermined" n)

    [ header; "" ] @ missing @ (p.Shortfalls |> List.collect (renderShortfall p))

/// PURE: the reviewable proposal.
let renderProposal (evidence: RunEvidence) (projects: ProjectProposal list) : string =
    let count pick =
        projects
        |> List.sumBy (fun p -> p.Shortfalls |> List.filter pick |> List.length)

    let total = count (fun _ -> true)
    let below = count (fun s -> s.Determined)
    let undetermined = total - below

    let failingProjects =
        projects
        |> List.filter (fun p -> p.Shortfalls |> List.exists (fun s -> s.Determined))

    let summary =
        match below, undetermined with
        | 0, 0 ->
            sprintf
                "Every measured file is at or above its floor in all %d project(s). Nothing to propose."
                projects.Length
        | _, 0 -> sprintf "%d file(s) below their floor across %d project(s)." below failingProjects.Length
        | _ ->
            sprintf
                "%d file(s) below their floor across %d project(s); %d more undetermined."
                below
                failingProjects.Length
                undetermined

    let preamble =
        [
            sprintf "# Coverage floor proposal from CI run %s" evidence.RunId
            ""
            sprintf "- run: %s" evidence.Url
            sprintf "- workflow: %s (%s)" evidence.WorkflowName evidence.Conclusion
            sprintf "- commit: %s" evidence.HeadSha
            ""
            summary
            ""
        ]

    let policy =
        if total = 0 then
            []
        else
            [
                "PROPOSAL ONLY: no floor file was changed and nothing was pushed."
                ""
                "Before applying an entry, prefer a test that closes the gap. A floor lowered for a"
                "gap only one platform sees stays an explained exception: extend its reason with why"
                "the gap exists before committing it."
                ""
            ]

    preamble @ (projects |> List.collect renderProject) @ policy
    |> String.concat "\n"

/// Run metadata from `gh run view`.
let parseRunEvidence (runId: string) (json: string) : RunEvidence =
    let root = JsonNode.Parse(json)

    let str (name: string) =
        match root.[name] with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
        | _ -> ""

    {
        RunId = runId
        HeadSha = str "headSha"
        Conclusion = str "conclusion"
        WorkflowName = str "workflowName"
        Url = str "url"
    }

let private isFloorFile (configPaths: string list) (outputPath: string) =
    let full = Path.GetFullPath outputPath
    let name = Path.GetFileName full

    (name.StartsWith("coverage-ratchet", StringComparison.Ordinal)
     && name.EndsWith(".json", StringComparison.Ordinal))
    || configPaths |> List.exists (fun c -> Path.GetFullPath c = full)

let private fetchProposal
    (run: string -> string -> CommandResult)
    (configDir: string)
    (defaultConfigPath: string)
    (runId: string)
    (downloadDir: string)
    : Result<RunEvidence * ProjectProposal list, string> =
    match run "gh" (sprintf "run view %s --json headSha,conclusion,workflowName,url" runId) with
    | Failure(msg, _) -> Error(sprintf "gh run view %s failed: %s" runId msg)
    | Success viewJson ->
        let evidence = parseRunEvidence runId viewJson

        match run "gh" (sprintf "run download %s -n %s -D %s" runId ArtifactName downloadDir) with
        | Failure(msg, _) -> Error(sprintf "could not download the '%s' artifact of run %s: %s" ArtifactName runId msg)
        | Success _ ->
            let files =
                if Directory.Exists downloadDir then
                    Directory.GetFiles(downloadDir, "coverage-thresholds-*.json")
                    |> Array.sort
                    |> Array.toList
                else
                    []

            if List.isEmpty files then
                Error(sprintf "the '%s' artifact of run %s holds no coverage-thresholds-*.json file" ArtifactName runId)
            else
                try
                    Ok(evidence, files |> List.map (judgeThresholdFile evidence configDir defaultConfigPath))
                with ex ->
                    Error(sprintf "could not read the artifact of run %s: %s" runId ex.Message)

/// Fetch run `runId`'s artifact, judge every project in it, and print the proposal
/// (or write it to `output`). Exit 0: nothing below a floor. Exit 1: a file is below,
/// or possibly below, its floor. Exit 2: the run could not be read, so nothing can be said.
///
/// `output` may not name a floor file: the command's one guarantee is that floor
/// files come out of it unchanged. `print` receives what would go to stdout.
let runProposeFromCi
    (run: string -> string -> CommandResult)
    (print: string -> unit)
    (configDir: string)
    (defaultConfigPath: string)
    (runId: string)
    (output: string option)
    : int =
    let downloadDir =
        Path.Combine(Path.GetTempPath(), sprintf "coverage-proposal-%s-%s" runId (Guid.NewGuid().ToString("N")))

    let refusedOutput =
        output
        |> Option.filter (isFloorFile [ Path.Combine(configDir, defaultConfigPath) ])

    if runId = "" || not (runId |> Seq.forall Char.IsDigit) then
        eprintfn "propose-from-ci: run id must be a GitHub Actions run number, got '%s'" runId
        2
    else
        match refusedOutput with
        | Some path ->
            eprintfn "propose-from-ci: refusing to write the proposal over floor file %s" path
            2
        | None ->
            try
                match fetchProposal run configDir defaultConfigPath runId downloadDir with
                | Error msg ->
                    eprintfn "propose-from-ci: %s" msg
                    2
                | Ok(evidence, projects) ->
                    let text = renderProposal evidence projects

                    match output with
                    | Some path ->
                        File.WriteAllText(path, text + "\n")
                        print (sprintf "Proposal written to %s" path)
                    | None -> print text

                    if projects |> List.exists (fun p -> not (List.isEmpty p.Shortfalls)) then
                        1
                    else
                        0
            finally
                if Directory.Exists downloadDir then
                    Directory.Delete(downloadDir, true)

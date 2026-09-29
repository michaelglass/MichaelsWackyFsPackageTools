module CoverageRatchet.Tests.ProposeFromCiTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.ProposeFromCi
open Tests.Common.TestHelpers

let private fixtureDir =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "propose-from-ci")

let private artifactFile name =
    Path.Combine(fixtureDir, "artifact", name)

let private evidence =
    {
        RunId = "36309004650"
        HeadSha = "a58a09c365dbc7f9c18b27a22da7e8fad30636be"
        Conclusion = "failure"
        WorkflowName = "CI"
        Url = "https://github.com/owner/repo/actions/runs/36309004650"
    }

let private viewJson =
    """{"conclusion":"failure","headSha":"a58a09c365dbc7f9c18b27a22da7e8fad30636be","url":"https://github.com/owner/repo/actions/runs/36309004650","workflowName":"CI"}"""

/// Copy the fixture floor files into a scratch repo directory.
let private withFixtureRepo (action: string -> 'a) =
    withTempDir (fun dir ->
        for f in Directory.GetFiles(Path.Combine(fixtureDir, "repo")) do
            File.Copy(f, Path.Combine(dir, Path.GetFileName f))

        action dir)

/// A `gh` stand-in that records every command and, on `run download`, copies the
/// fixture artifact into the `-D` directory the way `gh` would.
let private recordingGh (calls: ResizeArray<string * string>) (cmd: string) (args: string) =
    calls.Add((cmd, args))

    if cmd = "gh" && args.StartsWith("run view") then
        CoverageRatchet.Shell.Success viewJson
    elif cmd = "gh" && args.StartsWith("run download") then
        let dir = args.Substring(args.IndexOf(" -D ") + 4)
        Directory.CreateDirectory(dir) |> ignore

        for f in Directory.GetFiles(Path.Combine(fixtureDir, "artifact")) do
            File.Copy(f, Path.Combine(dir, Path.GetFileName f))

        CoverageRatchet.Shell.Success ""
    else
        CoverageRatchet.Shell.Failure("unexpected command", 1)

/// Collects what the command would print, without touching the process console
/// other test modules capture in parallel.
let private printed () =
    let lines = ResizeArray<string>()
    lines, (fun (line: string) -> lines.Add line)

let private snapshot (dir: string) =
    Directory.GetFiles(dir)
    |> Array.sort
    |> Array.map (fun f -> Path.GetFileName f, File.ReadAllBytes f)
    |> Array.toList

// --- fixture artifact ---

[<Fact>]
let ``fixture artifact parses into per-project platform results`` () =
    let platform, results =
        parseCiThresholds (File.ReadAllText(artifactFile "coverage-thresholds-Alpha.json"))

    test <@ platform = Linux @>
    test <@ results.["Daemon.fs"] = { Line = 91.0; Branch = 82.0 } @>
    test <@ projectOfThresholdFile (artifactFile "coverage-thresholds-Alpha.json") = "Alpha" @>

[<Fact>]
let ``configPathForProject maps default and named projects`` () =
    test <@ configPathForProject "coverage-ratchet.json" "default" = "coverage-ratchet.json" @>
    test <@ configPathForProject "coverage-ratchet.json" "" = "coverage-ratchet.json" @>
    test <@ configPathForProject "coverage-ratchet.json" "Beta" = "coverage-ratchet-Beta.json" @>

[<Fact>]
let ``parseRunEvidence reads gh run view output and tolerates missing fields`` () =
    test <@ parseRunEvidence "36309004650" viewJson = evidence @>

    let partial =
        parseRunEvidence "1" """{"headSha":"abc","conclusion":null,"workflowName":7}"""

    test
        <@
            partial.HeadSha = "abc"
            && partial.Conclusion = ""
            && partial.Url = ""
            && partial.WorkflowName = ""
        @>

[<Fact>]
let ``draftReason keeps a short commit id whole`` () =
    let shortfall =
        {
            File = "A.fs"
            Measured = { Line = 90.0; Branch = 80.0 }
            FloorLine = 95.0
            FloorBranch = 80.0
            Source = DefaultFloor
            PreviousReason = None
            Determined = true
            Proposed = []
        }

    let reason = draftReason { evidence with HeadSha = "abc" } MacOS shortfall 90.0 80.0

    test <@ reason.StartsWith("macos CI run 36309004650 (commit abc) measured line 90%, branch 80%") @>

[<Fact>]
let ``renderProposal with only undetermined files drafts nothing`` () =
    let s =
        {
            File = "A.fs"
            Measured = { Line = 69.0; Branch = 62.0 }
            FloorLine = 69.1
            FloorBranch = 62.0
            Source = SharedEntry
            PreviousReason = None
            Determined = false
            Proposed = []
        }

    let project =
        {
            Project = "P"
            ConfigPath = "coverage-ratchet-P.json"
            ConfigFound = true
            Platform = Linux
            FilesMeasured = 1
            Shortfalls = [ s ]
        }

    let text = renderProposal evidence [ project ]
    test <@ text.Contains("0 file(s) below their floor across 0 project(s); 1 more undetermined.") @>
    test <@ not (text.Contains("```json")) @>

// --- below-floor detection ---

[<Fact>]
let ``every file below its floor is found across both fixture projects`` () =
    withFixtureRepo (fun dir ->
        let alpha =
            judgeThresholdFile evidence dir "coverage-ratchet.json" (artifactFile "coverage-thresholds-Alpha.json")

        let beta =
            judgeThresholdFile evidence dir "coverage-ratchet.json" (artifactFile "coverage-thresholds-Beta.json")

        test <@ alpha.ConfigFound && beta.ConfigFound @>
        test <@ alpha.FilesMeasured = 3 && beta.FilesMeasured = 2 @>
        test <@ alpha.Shortfalls |> List.map (fun s -> s.File) = [ "Daemon.fs" ] @>
        test <@ beta.Shortfalls |> List.map (fun s -> s.File) = [ "SiteProbes.fs" ] @>

        let daemon = alpha.Shortfalls.Head
        test <@ daemon.Source = PlatformEntry @>
        test <@ (daemon.FloorLine, daemon.FloorBranch) = (91.0, 84.0) @>
        test <@ daemon.PreviousReason = Some "process supervision paths" @>

        let probes = beta.Shortfalls.Head
        test <@ probes.Source = DefaultFloor @>
        test <@ (probes.FloorLine, probes.FloorBranch) = (100.0, 100.0) @>)

[<Fact>]
let ``floors are those of the artifact's platform, not the machine reading them`` () =
    // A macOS-only floor is invisible to Linux CI: Linux falls back to 100/100.
    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides =
                Map.ofList
                    [
                        "Os.fs",
                        [
                            {
                                Line = 50.0
                                Branch = 50.0
                                Reason = None
                                Platform = Some MacOS
                            }
                        ]
                    ]
            RawCountFloors = Map.empty
        }

    let results = Map.ofList [ "Os.fs", { Line = 60.0; Branch = 60.0 } ]

    let onLinux = findShortfalls evidence Linux raw results
    let onMac = findShortfalls evidence MacOS raw results

    test <@ onLinux |> List.map (fun s -> s.File, s.Source) = [ "Os.fs", DefaultFloor ] @>
    test <@ List.isEmpty onMac @>

[<Fact>]
let ``a shared floor that fell gets a platform entry beside it, lowered only where it fell`` () =
    let shared =
        {
            Line = 85.0
            Branch = 70.0
            Reason = Some "probe timeouts"
            Platform = None
        }

    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.ofList [ "Probe.fs", [ shared ] ]
            RawCountFloors = Map.empty
        }

    let results = Map.ofList [ "Probe.fs", { Line = 80.6; Branch = 75.0 } ]

    match findShortfalls evidence Linux raw results with
    | [ s ] ->
        test <@ s.Source = SharedEntry && s.PreviousReason = None @>

        match s.Proposed with
        | [ kept; added ] ->
            test <@ kept = shared @>
            test <@ (added.Line, added.Branch, added.Platform) = (80.0, 70.0, Some Linux) @>
        | other -> failwithf "expected two entries, got %A" other
    | other -> failwithf "expected one shortfall, got %A" other

[<Fact>]
let ``a whole-number measurement against a fractional floor is undetermined, not drafted`` () =
    let floorOf line branch =
        [
            {
                Line = line
                Branch = branch
                Reason = None
                Platform = None
            }
        ]

    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides =
                Map.ofList
                    [
                        "Rounded.fs", floorOf 69.1 62.0
                        "Fell.fs", floorOf 70.0 62.0
                        "Exact.fs", floorOf 69.1 62.0
                    ]
            RawCountFloors = Map.empty
        }

    let results =
        Map.ofList
            [
                "Rounded.fs", { Line = 69.0; Branch = 62.0 }
                "Fell.fs", { Line = 69.0; Branch = 62.0 }
                "Exact.fs", { Line = 69.05; Branch = 62.0 }
            ]

    let found =
        findShortfalls evidence Linux raw results
        |> List.map (fun s -> s.File, s.Determined, List.length s.Proposed)

    test <@ found = [ "Exact.fs", true, 2; "Fell.fs", true, 2; "Rounded.fs", false, 0 ] @>

    let project =
        {
            Project = "P"
            ConfigPath = "coverage-ratchet-P.json"
            ConfigFound = true
            Platform = Linux
            FilesMeasured = 3
            Shortfalls = findShortfalls evidence Linux raw results
        }

    let text = renderProposal evidence [ project ]
    test <@ text.Contains("2 file(s) below their floor across 1 project(s); 1 more undetermined.") @>

    test
        <@ text.Contains("## coverage-ratchet-P.json — 2 of 3 measured file(s) below the linux floor, 1 undetermined") @>

    test <@ text.Contains("UNDETERMINED: the artifact rounds down to whole percentages") @>
    test <@ not (text.Contains("\"Rounded.fs\":")) @>

// --- proposal rendering ---

[<Fact>]
let ``the drafted entry replaces only the platform entry and cites run, commit and numbers`` () =
    withFixtureRepo (fun dir ->
        let alpha =
            judgeThresholdFile evidence dir "coverage-ratchet.json" (artifactFile "coverage-thresholds-Alpha.json")

        match alpha.Shortfalls.Head.Proposed with
        | [ mac; linux ] ->
            test <@ mac.Platform = Some MacOS && mac.Line = 93.0 && mac.Branch = 90.0 @>
            test <@ linux.Platform = Some Linux && linux.Line = 91.0 && linux.Branch = 82.0 @>

            test
                <@
                    linux.Reason =
                        Some(
                            "linux CI run 36309004650 (commit a58a09c3) measured line 91%, branch 82% "
                            + "against a floor of line 91%, branch 84%; floor set to the measured value, rounded down."
                        )
                @>
        | other -> failwithf "expected the macos and linux entries, got %A" other)

[<Fact>]
let ``renderProposal lists every shortfall with its evidence and a paste-ready entry`` () =
    withFixtureRepo (fun dir ->
        let projects =
            [ "coverage-thresholds-Alpha.json"; "coverage-thresholds-Beta.json" ]
            |> List.map (artifactFile >> judgeThresholdFile evidence dir "coverage-ratchet.json")

        let text = renderProposal evidence projects

        test <@ text.StartsWith("# Coverage floor proposal from CI run 36309004650\n") @>
        test <@ text.Contains("- commit: a58a09c365dbc7f9c18b27a22da7e8fad30636be") @>
        test <@ text.Contains("- workflow: CI (failure)") @>
        test <@ text.Contains("2 file(s) below their floor across 2 project(s).") @>
        test <@ text.Contains("## coverage-ratchet-Alpha.json — 1 of 3 measured file(s) below the linux floor") @>
        test <@ text.Contains("## coverage-ratchet-Beta.json — 1 of 2 measured file(s) below the linux floor") @>
        test <@ text.Contains("### Daemon.fs") && text.Contains("### SiteProbes.fs") @>
        test <@ not (text.Contains("### Probe.fs")) && not (text.Contains("### Shared.fs")) @>
        test <@ text.Contains("- linux floor: line 100%, branch 100% (the default; the file has no entry)") @>
        test <@ text.Contains("- reason it replaces: process supervision paths") @>
        test <@ text.Contains("PROPOSAL ONLY: no floor file was changed and nothing was pushed.") @>

        // The entry block is valid JSON once wrapped in braces, and matches what the
        // config writer would produce.
        let block =
            let start = text.IndexOf("\"SiteProbes.fs\": ")
            let stop = text.IndexOf("```", start)
            text.Substring(start, stop - start)

        use doc = System.Text.Json.JsonDocument.Parse("{" + block + "}")
        let entries = doc.RootElement.GetProperty("SiteProbes.fs")
        let count = entries.GetArrayLength()
        let branch = entries.[0].GetProperty("branch").GetDouble()
        let platform = entries.[0].GetProperty("platform").GetString()
        test <@ count = 1 && branch = 93.0 && platform = "linux" @>)

[<Fact>]
let ``renderProposal says so when nothing fell and flags a missing floor file`` () =
    withTempDir (fun dir ->
        let clean =
            {
                Project = "Alpha"
                ConfigPath = Path.Combine(dir, "coverage-ratchet-Alpha.json")
                ConfigFound = false
                Platform = Linux
                FilesMeasured = 4
                Shortfalls = []
            }

        let text = renderProposal evidence [ clean ]

        test
            <@ text.Contains("Every measured file is at or above its floor in all 1 project(s). Nothing to propose.") @>

        test <@ text.Contains("coverage-ratchet-Alpha.json was not found") @>
        test <@ not (text.Contains("PROPOSAL ONLY")) @>)

// --- the command never writes floor files ---

[<Fact>]
let ``runProposeFromCi prints the proposal, exits 1, and leaves every floor file untouched`` () =
    withFixtureRepo (fun dir ->
        let before = snapshot dir
        let calls = ResizeArray()

        let lines, print = printed ()

        let code =
            runProposeFromCi (recordingGh calls) print dir "coverage-ratchet.json" "36309004650" None

        let out = String.concat "\n" lines

        test <@ code = 1 @>
        test <@ out.Contains("### Daemon.fs") && out.Contains("### SiteProbes.fs") @>
        let after = snapshot dir
        test <@ after = before @>

        // Only the two read-only gh calls: no jj, no git, no push.
        match List.ofSeq calls with
        | [ ("gh", view); ("gh", download) ] ->
            test <@ view.StartsWith("run view 36309004650 ") @>
            test <@ download.StartsWith("run download 36309004650 -n coverage-thresholds -D ") @>

            let downloadDir = download.Substring(download.IndexOf(" -D ") + 4)
            test <@ not (Directory.Exists downloadDir) @>
        | other -> failwithf "expected exactly two gh calls, got %A" other)

[<Fact>]
let ``runProposeFromCi writes to a named output file and nothing else`` () =
    withFixtureRepo (fun dir ->
        let before = snapshot dir
        let outDir = createTempDir ()

        try
            let outPath = Path.Combine(outDir, "proposal.md")
            let calls = ResizeArray()

            let lines, print = printed ()

            let code =
                runProposeFromCi (recordingGh calls) print dir "coverage-ratchet.json" "36309004650" (Some outPath)

            test <@ List.ofSeq lines = [ sprintf "Proposal written to %s" outPath ] @>

            test <@ code = 1 @>
            test <@ File.ReadAllText(outPath).Contains("### SiteProbes.fs") @>
            let after = snapshot dir
            test <@ after = before @>
        finally
            cleanupDir outDir)

[<Fact>]
let ``runProposeFromCi refuses an output path that is a floor file`` () =
    withFixtureRepo (fun dir ->
        let before = snapshot dir
        let calls = ResizeArray()
        let target = Path.Combine(dir, "coverage-ratchet-Alpha.json")

        let code =
            runProposeFromCi (recordingGh calls) ignore dir "coverage-ratchet.json" "36309004650" (Some target)

        let after = snapshot dir
        let callCount = calls.Count
        test <@ code = 2 @>
        test <@ callCount = 0 @>
        test <@ after = before @>

        let defaultTarget = Path.Combine(dir, "coverage-ratchet.json")

        test <@ runProposeFromCi (recordingGh calls) ignore dir "coverage-ratchet.json" "1" (Some defaultTarget) = 2 @>)

[<Fact>]
let ``runProposeFromCi rejects a run id that is not a number`` () =
    let calls = ResizeArray()

    test <@ runProposeFromCi (recordingGh calls) ignore "." "coverage-ratchet.json" "1; rm -rf /" None = 2 @>
    test <@ runProposeFromCi (recordingGh calls) ignore "." "coverage-ratchet.json" "" None = 2 @>
    let callCount = calls.Count
    test <@ callCount = 0 @>

[<Fact>]
let ``runProposeFromCi exits 0 when every measured file held its floor`` () =
    withTempDir (fun dir ->
        let run (cmd: string) (args: string) =
            if args.StartsWith("run view") then
                CoverageRatchet.Shell.Success viewJson
            else
                let d = args.Substring(args.IndexOf(" -D ") + 4)
                Directory.CreateDirectory(d) |> ignore

                File.WriteAllText(
                    Path.Combine(d, "coverage-thresholds-default.json"),
                    """{"platform":"linux","results":{"A.fs":{"line":100,"branch":100}}}"""
                )

                CoverageRatchet.Shell.Success ""

        let lines, print = printed ()
        let code = runProposeFromCi run print dir "coverage-ratchet.json" "7" None
        let out = String.concat "\n" lines

        test <@ code = 0 @>
        test <@ out.Contains("Nothing to propose.") @>)

[<Fact>]
let ``runProposeFromCi exits 2 when the run or its artifact cannot be read`` () =
    let viewFails _ _ =
        CoverageRatchet.Shell.Failure("run not found", 1)

    let downloadFails (_: string) (args: string) =
        if args.StartsWith("run view") then
            CoverageRatchet.Shell.Success viewJson
        else
            CoverageRatchet.Shell.Failure("no artifact", 1)

    let emptyArtifact (_: string) (args: string) =
        if args.StartsWith("run view") then
            CoverageRatchet.Shell.Success viewJson
        else
            CoverageRatchet.Shell.Success ""

    let corruptArtifact (_: string) (args: string) =
        if args.StartsWith("run view") then
            CoverageRatchet.Shell.Success viewJson
        else
            let d = args.Substring(args.IndexOf(" -D ") + 4)
            Directory.CreateDirectory(d) |> ignore
            File.WriteAllText(Path.Combine(d, "coverage-thresholds-X.json"), "{not json")
            CoverageRatchet.Shell.Success ""

    withTempDir (fun dir ->
        for run in [ viewFails; downloadFails; emptyArtifact; corruptArtifact ] do
            let code = runProposeFromCi run ignore dir "coverage-ratchet.json" "5" None

            test <@ code = 2 @>)

[<Fact>]
let ``main parses propose-from-ci with its run id`` () =
    let result = CoverageRatchet.Program.main [| "propose-from-ci"; "--help" |]
    test <@ result = 0 @>

    let refused = CoverageRatchet.Program.main [| "propose-from-ci"; "not-a-number" |]

    test <@ refused = 2 @>

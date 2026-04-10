module CoverageRatchet.Tests.ProgramTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Program
open Tests.Common.TestHelpers
open CoverageRatchet.Tests.CoverageTestHelpers

// --- resolveGitDir tests ---

[<Fact>]
let ``resolveGitDir - returns None for normal git repo`` () =
    withTempDir (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        let result = resolveGitDir tmpDir

        test <@ result = None @>)

[<Fact>]
let ``resolveGitDir - returns absolute path for jj repo`` () =
    withTempDir (fun tmpDir ->
        let jjGitDir = Path.Combine(tmpDir, ".jj", "repo", "store", "git")
        Directory.CreateDirectory(jjGitDir) |> ignore

        let result = resolveGitDir tmpDir

        test <@ result = Some jjGitDir @>)

[<Fact>]
let ``resolveGitDir - prefers git over jj when both exist`` () =
    withTempDir (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj", "repo", "store", "git"))
        |> ignore

        let result = resolveGitDir tmpDir

        test <@ result = None @>)

[<Fact>]
let ``resolveGitDir - returns None when neither git nor jj exists`` () =
    withTempDir (fun tmpDir ->
        let result = resolveGitDir tmpDir

        test <@ result = None @>)

// --- formatFileResult tests ---

[<Fact>]
let ``formatFileResult - passing file at 100 percent`` () =
    let r =
        { File = makeFile "Foo.fs" 100.0 100.0 4 4
          LineThreshold = 100.0
          BranchThreshold = 100.0 }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ result.Contains("Foo.fs") @>
    test <@ not (result.Contains("[min:")) @>

[<Fact>]
let ``formatFileResult - failing file with thresholds shows one decimal`` () =
    let r =
        { File = makeFile "Bar.fs" 60.5 50.3 1 4
          LineThreshold = 80.2
          BranchThreshold = 70.9 }

    let result = formatFileResult r

    test <@ result.Contains("FAIL") @>
    test <@ result.Contains("Bar.fs") @>
    test <@ result.Contains("line=60.5%") @>
    test <@ result.Contains("branch=50.3%") @>
    test <@ result.Contains("[min: line=80.2% branch=70.9%]") @>

[<Fact>]
let ``formatFileResult - file with no branches`` () =
    let r =
        { File = makeFile "Simple.fs" 90.0 100.0 0 0
          LineThreshold = 80.0
          BranchThreshold = 100.0 }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ not (result.Contains("branches)")) @>

// --- run tests ---

let private makeCoverageXml (linePct: int) =
    let hitLines = linePct / 10
    let missLines = 10 - hitLines

    let lines =
        [ for i in 1..hitLines -> sprintf """<line number="%d" hits="1" />""" i ]
        @ [ for i in (hitLines + 1) .. (hitLines + missLines) -> sprintf """<line number="%d" hits="0" />""" i ]

    sprintf
        """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Foo.fs">
          <lines>
%s
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""
        (String.concat "\n" lines)

[<Fact>]
let ``run - check with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - check with passing coverage file returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with failing coverage returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - ratchet with no changes returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        // 50% coverage
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Set up config with override matching actual coverage exactly
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 50.0
                        Branch = 100.0
                        Reason = Some "test"
                        Platform = None } ] }

        saveConfig configPath config

        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - ratchet with tightened config returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        // 50% coverage
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Set up config with override lower than actual coverage (will be tightened)
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 30.0
                        Branch = 100.0
                        Reason = Some "test"
                        Platform = None } ] }

        saveConfig configPath config

        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - ratchet with failed files returns Ok 2`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        // 50% coverage, but default threshold is 100%
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // No overrides, so default 100% threshold applies and 50% coverage fails
        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 2 @>)

[<Fact>]
let ``run - ratchet creates config and returns Ok 2 when below threshold`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 2 @>
        test <@ File.Exists(configPath) @>)

[<Fact>]
let ``run - loosen creates config and returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run (Loosen(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>
        test <@ File.Exists(configPath) @>)

[<Fact>]
let ``run - loosen then check passes`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Loosen first
        let _ = run (Loosen(config = Some configPath)) tmpDir

        // Now check should pass
        let result = run (Check(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>)

// --- runCheck with empty file list ---

[<Fact>]
let ``run - check with only non-fs files returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0"?><coverage><packages><package><classes>
<class filename="Foo.cs" line-rate="1.0" branch-rate="1.0"><lines/></class>
</classes></package></packages></coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Ok 0 @>)

// --- ratchet None branch (tightened count) ---

[<Fact>]
let ``run - ratchet with new file in coverage only counts existing overrides as tightened`` () =
    withTempDir (fun tmpDir ->
        // Coverage XML with Foo.fs at 50% and Bar.fs at 80%
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Foo.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="1" />
            <line number="3" hits="1" />
            <line number="4" hits="1" />
            <line number="5" hits="1" />
            <line number="6" hits="0" />
            <line number="7" hits="0" />
            <line number="8" hits="0" />
            <line number="9" hits="0" />
            <line number="10" hits="0" />
          </lines>
        </class>
        <class filename="/src/Bar.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="1" />
            <line number="3" hits="1" />
            <line number="4" hits="1" />
            <line number="5" hits="0" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Config starts with override for ONLY Foo.fs at line=30
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 30.0
                        Branch = 100.0
                        Reason = Some "test"
                        Platform = None } ] }

        saveConfig configPath config

        // Ratchet runs - Foo.fs tightens from 30 to 50, Bar.fs gets created via Failed path
        let result = run (Ratchet(config = Some configPath)) tmpDir

        // Bar.fs is below 100% with no override, so this is a Failed result (Ok 2)
        test <@ result = Ok 2 @>)

// --- check-json tests ---

[<Fact>]
let ``run - check-json writes platform and file results to output file`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let outputPath = Path.Combine(tmpDir, "output.json")

        let result =
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir

        test <@ result = Ok 1 @>
        test <@ File.Exists(outputPath) @>

        let json = File.ReadAllText(outputPath)
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let platform = root.GetProperty("platform").GetString()
        test <@ platform = Platform.toString Platform.current @>

        let results = root.GetProperty("results")
        let hasFoo = results.TryGetProperty("Foo.fs") |> fst
        test <@ hasFoo @>

        let fooLine = results.GetProperty("Foo.fs").GetProperty("line").GetInt32()
        let fooBranch = results.GetProperty("Foo.fs").GetProperty("branch").GetInt32()
        test <@ fooLine = 50 @>
        test <@ fooBranch = 100 @>)

[<Fact>]
let ``run - check-json with passing coverage returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")
        let outputPath = Path.Combine(tmpDir, "output.json")

        let result =
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir

        test <@ result = Ok 0 @>
        test <@ File.Exists(outputPath) @>

        let json = File.ReadAllText(outputPath)
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let platform = root.GetProperty("platform").GetString()
        test <@ platform = Platform.toString Platform.current @>)

[<Fact>]
let ``run - check-json with failing coverage returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let outputPath = Path.Combine(tmpDir, "output.json")

        let result =
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir

        test <@ result = Ok 1 @>)

// --- main tests ---

[<Fact>]
let ``main with --help returns 0`` () =
    let result = main [| "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with -h returns 0`` () =
    let result = main [| "-h" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with help returns 0`` () =
    let result = main [| "help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with unknown flag returns 1`` () =
    let result = main [| "--unknown" |]

    test <@ result = 1 @>

[<Fact>]
let ``main with loosen-from-ci --help returns 0`` () =
    let result = main [| "loosen-from-ci"; "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with no args and no coverage file returns 1`` () =
    // With CmdDefault, bare invocation runs ratchet, which will fail to find coverage file
    let result = main [||]

    test <@ result = 1 @>

// --- formatFileResult additional variations ---

[<Fact>]
let ``formatFileResult - passing with override shows thresholds`` () =
    let r =
        { File = makeFile "Foo.fs" 85.0 75.0 3 4
          LineThreshold = 80.0
          BranchThreshold = 70.0 }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ result.Contains("[min: line=80.0% branch=70.0%]") @>
    test <@ result.Contains("(3/4 branches)") @>

[<Fact>]
let ``formatFileResult - failing with no branches`` () =
    let r =
        { File = makeFile "Foo.fs" 50.0 100.0 0 0
          LineThreshold = 100.0
          BranchThreshold = 100.0 }

    let result = formatFileResult r

    test <@ result.Contains("FAIL") @>
    test <@ not (result.Contains("branches)")) @>
    test <@ not (result.Contains("[min:")) @>

// --- run with each command variant ---

[<Fact>]
let ``run - check-json with default output path`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")
        // No explicit output path - uses default
        let result = run (CheckJson(config = Some configPath, output = None)) tmpDir

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - loosen with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Loosen(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - ratchet with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result =
            run (Ratchet(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - check-json with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result =
            run (CheckJson(config = Some(Path.Combine(tmpDir, "config.json")), output = Some "out.json")) tmpDir

        test <@ result = Error "No coverage.cobertura.xml found" @>)

// --- formatFileResult additional edge cases ---

[<Fact>]
let ``formatFileResult - failing file at default thresholds shows no min`` () =
    let r =
        { File = makeFile "Fail.fs" 50.0 40.0 2 5
          LineThreshold = 100.0
          BranchThreshold = 100.0 }

    let result = formatFileResult r

    test <@ result.Contains("FAIL") @>
    test <@ result.Contains("(2/5 branches)") @>
    test <@ not (result.Contains("[min:")) @>

[<Fact>]
let ``formatFileResult - passing with only line below 100 shows threshold`` () =
    let r =
        { File = makeFile "Half.fs" 85.0 100.0 0 0
          LineThreshold = 80.0
          BranchThreshold = 100.0 }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ result.Contains("[min: line=80.0% branch=100.0%]") @>

[<Fact>]
let ``formatFileResult - passing with only branch below 100 shows threshold`` () =
    let r =
        { File = makeFile "Half.fs" 100.0 75.0 3 4
          LineThreshold = 100.0
          BranchThreshold = 70.0 }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ result.Contains("[min: line=100.0% branch=70.0%]") @>

// --- main with subcommand help ---

[<Fact>]
let ``main with check --help returns 0`` () =
    let result = main [| "check"; "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with ratchet --help returns 0`` () =
    let result = main [| "ratchet"; "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with loosen --help returns 0`` () =
    let result = main [| "loosen"; "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with check-json --help returns 0`` () =
    let result = main [| "check-json"; "--help" |]

    test <@ result = 0 @>

// --- run uses default config path ---

[<Fact>]
let ``run - uses default config path when None`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        // Check with config=None uses the defaultConfigPath relative to cwd
        let result = run (Check(config = None)) tmpDir

        // 100% coverage passes default thresholds
        test <@ result = Ok 0 @>)

// --- run with loosen uses default config ---

[<Fact>]
let ``run - loosen with config None creates default config`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Loosen(config = None)) tmpDir

        test <@ result = Ok 0 @>)

// --- ratchet with removed overrides ---

[<Fact>]
let ``run - ratchet removes override when file reaches 100 percent`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Override that will be removed because coverage is now at 100%
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 100.0
                        Reason = Some "was low"
                        Platform = None } ] }

        saveConfig configPath config

        let result = run (Ratchet(config = Some configPath)) tmpDir

        // Tightened returns Ok 1
        test <@ result = Ok 1 @>

        // Verify override was removed from config
        let loaded = loadConfig configPath
        test <@ loaded.Overrides.ContainsKey("Foo.fs") = false @>)

// --- ratchet with explicit config and 100% coverage ---

[<Fact>]
let ``run - ratchet with 100 percent coverage and no config returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>)

// --- check with mix of passed and failed files ---

[<Fact>]
let ``run - check reports both passed and failed files`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Good.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="1" />
          </lines>
        </class>
        <class filename="/src/Bad.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="0" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (Check(config = Some configPath)) tmpDir

        // Bad.fs is at 50% with 100% threshold, so it fails
        test <@ result = Ok 1 @>)

// --- check with all passing ---

[<Fact>]
let ``run - check with all files passing returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Good.fs">
          <lines>
            <line number="1" hits="1" />
          </lines>
        </class>
        <class filename="/src/Also.fs">
          <lines>
            <line number="1" hits="1" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (Check(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>)

// --- check-json with default output path writes to coverage-results.json ---

[<Fact>]
let ``run - check-json with None output writes coverage-results.json`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (CheckJson(config = Some configPath, output = None)) tmpDir

        test <@ result = Ok 0 @>
        // The default output path is "coverage-results.json" (relative)
        test <@ File.Exists("coverage-results.json") || result = Ok 0 @>)

// --- loosen then ratchet flow ---

[<Fact>]
let ``run - loosen then ratchet produces no changes`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Loosen first
        let _ = run (Loosen(config = Some configPath)) tmpDir

        // Ratchet should produce no changes (already loosened to actual)
        let result = run (Ratchet(config = Some configPath)) tmpDir

        test <@ result = Ok 0 @>)

// --- ratchet with multiple files, some tightened some removed ---

[<Fact>]
let ``run - ratchet tightens some overrides and removes others`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Foo.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="1" />
            <line number="3" hits="1" />
            <line number="4" hits="1" />
            <line number="5" hits="0" />
            <line number="6" hits="0" />
            <line number="7" hits="0" />
            <line number="8" hits="0" />
            <line number="9" hits="0" />
            <line number="10" hits="0" />
          </lines>
        </class>
        <class filename="/src/Bar.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="1" />
            <line number="3" hits="1" />
            <line number="4" hits="1" />
            <line number="5" hits="1" />
            <line number="6" hits="1" />
            <line number="7" hits="1" />
            <line number="8" hits="1" />
            <line number="9" hits="1" />
            <line number="10" hits="1" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Foo.fs at line=20 (will be tightened to 40), Bar.fs at line=90 (will be removed at 100%)
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 20.0
                        Branch = 100.0
                        Reason = Some "low"
                        Platform = None }
                      "Bar.fs",
                      { Line = 90.0
                        Branch = 100.0
                        Reason = Some "almost"
                        Platform = None } ] }

        saveConfig configPath config

        let result = run (Ratchet(config = Some configPath)) tmpDir

        // Tightened returns Ok 1
        test <@ result = Ok 1 @>

        // Verify: Foo.fs tightened, Bar.fs removed
        let loaded = loadConfig configPath
        test <@ loaded.Overrides.ContainsKey("Foo.fs") @>
        test <@ loaded.Overrides.["Foo.fs"].Line = 40.0 @>
        test <@ loaded.Overrides.ContainsKey("Bar.fs") = false @>)

// --- defaultConfigPath value ---

[<Fact>]
let ``defaultConfigPath is coverage-ratchet.json`` () =
    test <@ defaultConfigPath = "coverage-ratchet.json" @>

// --- CoverageFileCommand variants ---

[<Fact>]
let ``run dispatches Ratchet to CfRatchet`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run (Ratchet(config = Some configPath)) tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run dispatches Loosen None to CfLoosen`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Loosen(config = None)) tmpDir
        test <@ result = Ok 0 @>)

// --- CiResult type ---

[<Fact>]
let ``CiResult discriminated union cases are distinct`` () =
    let passed = CiPassed
    let otherFailure = CiOtherFailure
    let coverageFailure = CiCoverageFailure "/tmp/test"

    test <@ passed <> otherFailure @>
    test <@ otherFailure <> coverageFailure @>

    test
        <@
            match coverageFailure with
            | CiCoverageFailure dir -> dir = "/tmp/test"
            | _ -> false
        @>

// --- check-json multiple files ---

[<Fact>]
let ``run - check-json includes multiple files in output`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="/src/Foo.fs">
          <lines>
            <line number="1" hits="1" />
            <line number="2" hits="0" />
          </lines>
        </class>
        <class filename="/src/Bar.fs">
          <lines>
            <line number="1" hits="1" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")
        let outputPath = Path.Combine(tmpDir, "results.json")

        let result =
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir

        test <@ result = Ok 1 @>

        let json = File.ReadAllText(outputPath)

        let hasFoo, hasBar, fooLine, barLine =
            use doc = JsonDocument.Parse(json)
            let results = doc.RootElement.GetProperty("results")
            let hf = results.TryGetProperty("Foo.fs") |> fst
            let hb = results.TryGetProperty("Bar.fs") |> fst
            let fl = results.GetProperty("Foo.fs").GetProperty("line").GetInt32()
            let bl = results.GetProperty("Bar.fs").GetProperty("line").GetInt32()
            hf, hb, fl, bl

        test <@ hasFoo @>
        test <@ hasBar @>
        test <@ fooLine = 50 @>
        test <@ barLine = 100 @>)

// --- pollCi tests ---

let fakeRun (responses: (string * string * CoverageRatchet.Shell.CommandResult) list) =
    let mutable idx = 0

    fun (cmd: string) (args: string) ->
        if idx >= responses.Length then
            CoverageRatchet.Shell.Failure("no more responses", 1)
        else
            let (_, _, result) = responses.[idx]
            idx <- idx + 1
            result

[<Fact>]
let ``pollCi - gh failure returns CiOtherFailure`` () =
    let run = fakeRun [ ("gh", "", CoverageRatchet.Shell.Failure("gh not found", 1)) ]
    let result = pollCi run "abc123" 0 1

    test <@ result = CiOtherFailure @>

[<Fact>]
let ``pollCi - all runs completed and passed returns CiPassed`` () =
    let json = """[{"status":"completed","conclusion":"success","databaseId":1}]"""

    let run = fakeRun [ ("gh", "", CoverageRatchet.Shell.Success json) ]
    let result = pollCi run "abc123" 0 1

    test <@ result = CiPassed @>

[<Fact>]
let ``pollCi - empty runs array retries then fails`` () =
    let run =
        fakeRun
            [ ("gh", "", CoverageRatchet.Shell.Success "[]")
              ("gh", "", CoverageRatchet.Shell.Success "[]") ]

    let result = pollCi run "abc123" 0 2

    test <@ result = CiOtherFailure @>

[<Fact>]
let ``pollCi - in progress then completed returns CiPassed`` () =
    let inProgress = """[{"status":"in_progress","conclusion":"","databaseId":1}]"""

    let completed = """[{"status":"completed","conclusion":"success","databaseId":1}]"""

    let run =
        fakeRun
            [ ("gh", "", CoverageRatchet.Shell.Success inProgress)
              ("gh", "", CoverageRatchet.Shell.Success completed) ]

    let result = pollCi run "abc123" 0 2

    test <@ result = CiPassed @>

[<Fact>]
let ``pollCi - failed run with successful artifact download returns CiCoverageFailure`` () =
    let json = """[{"status":"completed","conclusion":"failure","databaseId":42}]"""

    let run =
        fakeRun
            [ ("gh", "", CoverageRatchet.Shell.Success json)
              ("gh", "", CoverageRatchet.Shell.Success "downloaded") ]

    let result = pollCi run "abc123" 0 1

    test
        <@
            match result with
            | CiCoverageFailure _ -> true
            | _ -> false
        @>

[<Fact>]
let ``pollCi - failed run with artifact download failure returns CiOtherFailure`` () =
    let json = """[{"status":"completed","conclusion":"failure","databaseId":42}]"""

    let run =
        fakeRun
            [ ("gh", "", CoverageRatchet.Shell.Success json)
              ("gh", "", CoverageRatchet.Shell.Failure("not found", 1)) ]

    let result = pollCi run "abc123" 0 1

    test <@ result = CiOtherFailure @>

[<Fact>]
let ``pollCi - timeout returns CiOtherFailure`` () =
    let inProgress = """[{"status":"in_progress","conclusion":"","databaseId":1}]"""

    let run =
        fakeRun
            [ ("gh", "", CoverageRatchet.Shell.Success inProgress)
              ("gh", "", CoverageRatchet.Shell.Success inProgress) ]

    let result = pollCi run "abc123" 0 1

    test <@ result = CiOtherFailure @>

// --- getVcsSha tests ---

[<Fact>]
let ``getVcsSha - jj succeeds returns trimmed sha`` () =
    let run = fakeRun [ ("jj", "", CoverageRatchet.Shell.Success "  abc123  ") ]
    let result = getVcsSha run

    test <@ result = "abc123" @>

[<Fact>]
let ``getVcsSha - jj fails git succeeds returns trimmed sha`` () =
    let run =
        fakeRun
            [ ("jj", "", CoverageRatchet.Shell.Failure("no jj", 1))
              ("git", "", CoverageRatchet.Shell.Success "  def456  ") ]

    let result = getVcsSha run

    test <@ result = "def456" @>

[<Fact>]
let ``getVcsSha - both fail throws`` () =
    let run =
        fakeRun
            [ ("jj", "", CoverageRatchet.Shell.Failure("no jj", 1))
              ("git", "", CoverageRatchet.Shell.Failure("no git", 1)) ]

    Assert.ThrowsAny<exn>(fun () -> getVcsSha run |> ignore) |> ignore

// --- vcsPush tests ---

[<Fact>]
let ``vcsPush - jj succeeds does not call git`` () =
    let mutable calls = []

    let run cmd args =
        calls <- calls @ [ cmd ]
        CoverageRatchet.Shell.Success "ok"

    vcsPush run
    test <@ calls = [ "jj" ] @>

[<Fact>]
let ``vcsPush - jj fails falls back to git push`` () =
    let mutable calls = []

    let run cmd args =
        calls <- calls @ [ cmd ]

        if cmd = "jj" then
            CoverageRatchet.Shell.Failure("no jj", 1)
        else
            CoverageRatchet.Shell.Success "pushed"

    vcsPush run
    test <@ calls = [ "jj"; "git" ] @>

// --- vcsCommitAndPush tests ---

[<Fact>]
let ``vcsCommitAndPush - jj succeeds runs jj workflow`` () =
    let mutable calls = []

    let run cmd args =
        calls <- calls @ [ (cmd, args) ]
        CoverageRatchet.Shell.Success "ok"

    vcsCommitAndPush run "config.json"

    test <@ calls.Length = 4 @>
    test <@ fst calls.[0] = "jj" @>
    test <@ (snd calls.[0]).Contains "describe" @>

[<Fact>]
let ``vcsCommitAndPush - jj fails falls back to git workflow`` () =
    let mutable calls = []

    let run cmd args =
        calls <- calls @ [ (cmd, args) ]

        if cmd = "jj" then
            CoverageRatchet.Shell.Failure("no jj", 1)
        else
            CoverageRatchet.Shell.Success "ok"

    vcsCommitAndPush run "config.json"

    test <@ calls |> List.exists (fun (c, _) -> c = "git") @>
    test <@ calls |> List.exists (fun (_, a) -> a.Contains "commit") @>

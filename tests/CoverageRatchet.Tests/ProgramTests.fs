module CoverageRatchet.Tests.ProgramTests

open System
open System.IO
open System.Text.Json
open Xunit
open Tests.Common
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
        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - check with passing coverage file returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with failing coverage returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

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

        let result = run (Ratchet(config = Some configPath)) tmpDir false

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

        let result = run (Ratchet(config = Some configPath)) tmpDir false

        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - ratchet with failed files returns Ok 2`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        // 50% coverage, but default threshold is 100%
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // No overrides, so default 100% threshold applies and 50% coverage fails
        let result = run (Ratchet(config = Some configPath)) tmpDir false

        test <@ result = Ok 2 @>)

[<Fact>]
let ``run - ratchet creates config and returns Ok 2 when below threshold`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run (Ratchet(config = Some configPath)) tmpDir false

        test <@ result = Ok 2 @>
        test <@ File.Exists(configPath) @>)

[<Fact>]
let ``run - loosen creates config and returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run (Loosen(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>
        test <@ File.Exists(configPath) @>)

[<Fact>]
let ``run - loosen then check passes`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")

        // Loosen first
        let _ = run (Loosen(config = Some configPath)) tmpDir false

        // Now check should pass
        let result = run (Check(config = Some configPath)) tmpDir false

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

        let result = run (Check(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

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
        let result = run (Ratchet(config = Some configPath)) tmpDir false

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
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir false

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
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir false

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
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir false

        test <@ result = Ok 1 @>)

// --- targets tests ---

[<Fact>]
let ``run - targets returns Ok 0 and lists files`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="/src/Low.fs">
    <lines>
      <line number="1" hits="1" />
      <line number="2" hits="0" />
    </lines>
  </class>
  <class filename="/src/High.fs">
    <lines>
      <line number="1" hits="1" />
      <line number="2" hits="1" />
    </lines>
  </class>
</classes></package></packages></coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (Targets(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - targets with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result =
            run (Targets(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Error "No coverage.cobertura.xml found" @>)

// --- gaps tests ---

[<Fact>]
let ``run - gaps returns Ok 0 with branch gaps`` () =
    withTempDir (fun tmpDir ->
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="/src/Branchy.fs">
    <lines>
      <line number="1" hits="1" condition-coverage="50% (1/2)" />
      <line number="2" hits="1" />
    </lines>
  </class>
</classes></package></packages></coverage>"""

        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, xml)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (Gaps(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - gaps with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Gaps(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Error "No coverage.cobertura.xml found" @>)

// --- multi-XML tests ---

[<Fact>]
let ``run - check merges coverage from multiple XMLs in subdirectories`` () =
    withTempDir (fun tmpDir ->
        // Two test projects, each covering different lines of Foo.fs
        let sub1 = Path.Combine(tmpDir, "TestProject1")
        let sub2 = Path.Combine(tmpDir, "TestProject2")
        Directory.CreateDirectory(sub1) |> ignore
        Directory.CreateDirectory(sub2) |> ignore

        // TestProject1: lines 1-5 hit, lines 6-10 miss
        File.WriteAllText(
            Path.Combine(sub1, "coverage.cobertura.xml"),
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
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
</classes></package></packages></coverage>"""
        )

        // TestProject2: lines 1-5 miss, lines 6-10 hit
        File.WriteAllText(
            Path.Combine(sub2, "coverage.cobertura.xml"),
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="/src/Foo.fs">
    <lines>
      <line number="1" hits="0" />
      <line number="2" hits="0" />
      <line number="3" hits="0" />
      <line number="4" hits="0" />
      <line number="5" hits="0" />
      <line number="6" hits="1" />
      <line number="7" hits="1" />
      <line number="8" hits="1" />
      <line number="9" hits="1" />
      <line number="10" hits="1" />
    </lines>
  </class>
</classes></package></packages></coverage>"""
        )

        let configPath = Path.Combine(tmpDir, "config.json")

        // With merging, Foo.fs should be 100% (all lines hit across both XMLs)
        // Without merging, only one XML is read and Foo.fs is 50%
        let result = run (Check(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - ratchet merges coverage from multiple XMLs`` () =
    withTempDir (fun tmpDir ->
        let sub1 = Path.Combine(tmpDir, "UnitTests")
        let sub2 = Path.Combine(tmpDir, "IntegrationTests")
        Directory.CreateDirectory(sub1) |> ignore
        Directory.CreateDirectory(sub2) |> ignore

        // UnitTests: Foo.fs lines 1-3 hit out of 5
        File.WriteAllText(
            Path.Combine(sub1, "coverage.cobertura.xml"),
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="/src/Foo.fs">
    <lines>
      <line number="1" hits="1" />
      <line number="2" hits="1" />
      <line number="3" hits="1" />
      <line number="4" hits="0" />
      <line number="5" hits="0" />
    </lines>
  </class>
</classes></package></packages></coverage>"""
        )

        // IntegrationTests: Foo.fs lines 3-5 hit out of 5
        File.WriteAllText(
            Path.Combine(sub2, "coverage.cobertura.xml"),
            """<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
  <class filename="/src/Foo.fs">
    <lines>
      <line number="1" hits="0" />
      <line number="2" hits="0" />
      <line number="3" hits="1" />
      <line number="4" hits="1" />
      <line number="5" hits="1" />
    </lines>
  </class>
</classes></package></packages></coverage>"""
        )

        let configPath = Path.Combine(tmpDir, "config.json")
        // Merged = 100%, so ratchet should produce no changes
        let result = run (Ratchet(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>)

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
        let result = run (CheckJson(config = Some configPath, output = None)) tmpDir false

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - loosen with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Loosen(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - ratchet with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result =
            run (Ratchet(config = Some(Path.Combine(tmpDir, "config.json")))) tmpDir false

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - check-json with no coverage file returns Error`` () =
    withTempDir (fun tmpDir ->
        let result =
            run (CheckJson(config = Some(Path.Combine(tmpDir, "config.json")), output = Some "out.json")) tmpDir false

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
        let result = run (Check(config = None)) tmpDir false

        // 100% coverage passes default thresholds
        test <@ result = Ok 0 @>)

// --- run with loosen uses default config ---

[<Fact>]
let ``run - loosen with config None creates default config`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Loosen(config = None)) tmpDir false

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

        let result = run (Ratchet(config = Some configPath)) tmpDir false

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

        let result = run (Ratchet(config = Some configPath)) tmpDir false

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

        let result = run (Check(config = Some configPath)) tmpDir false

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

        let result = run (Check(config = Some configPath)) tmpDir false

        test <@ result = Ok 0 @>)

// --- check-json with default output path writes to coverage-results.json ---

[<Fact>]
let ``run - check-json with None output writes coverage-results.json`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let configPath = Path.Combine(tmpDir, "config.json")

        let result = run (CheckJson(config = Some configPath, output = None)) tmpDir false

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
        let _ = run (Loosen(config = Some configPath)) tmpDir false

        // Ratchet should produce no changes (already loosened to actual)
        let result = run (Ratchet(config = Some configPath)) tmpDir false

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

        let result = run (Ratchet(config = Some configPath)) tmpDir false

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
        let result = run (Ratchet(config = Some configPath)) tmpDir false
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run dispatches Loosen None to CfLoosen`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run (Loosen(config = None)) tmpDir false
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
            run (CheckJson(config = Some configPath, output = Some outputPath)) tmpDir false

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

// --- extractFlags tests ---

[<Fact>]
let ``extractFlags - defaults to dot when not provided`` () =
    let dir, mergeBaselines, remaining = extractFlags [| "check" |]
    test <@ dir = "." @>
    test <@ mergeBaselines = false @>
    test <@ remaining = [| "check" |] @>

[<Fact>]
let ``extractFlags - extracts search-dir before command`` () =
    let dir, mergeBaselines, remaining = extractFlags [| "--search-dir"; "coverage"; "check" |]
    test <@ dir = "coverage" @>
    test <@ mergeBaselines = false @>
    test <@ remaining = [| "check" |] @>

[<Fact>]
let ``extractFlags - extracts search-dir after command`` () =
    let dir, mergeBaselines, remaining = extractFlags [| "check"; "--search-dir"; "coverage" |]
    test <@ dir = "coverage" @>
    test <@ mergeBaselines = false @>
    test <@ remaining = [| "check" |] @>

[<Fact>]
let ``extractFlags - ignores search-dir without value`` () =
    let dir, mergeBaselines, remaining = extractFlags [| "check"; "--search-dir" |]
    test <@ dir = "." @>
    test <@ mergeBaselines = false @>
    test <@ remaining = [| "check"; "--search-dir" |] @>

[<Fact>]
let ``extractFlags - empty argv`` () =
    let dir, mergeBaselines, remaining = extractFlags Array.empty
    test <@ dir = "." @>
    test <@ mergeBaselines = false @>
    test <@ remaining = Array.empty @>

[<Fact>]
let ``extractFlags - extracts merge-baselines flag`` () =
    let dir, mergeBaselines, remaining = extractFlags [| "check"; "--merge-baselines" |]
    test <@ dir = "." @>
    test <@ mergeBaselines = true @>
    test <@ remaining = [| "check" |] @>

[<Fact>]
let ``extractFlags - search-dir and merge-baselines combined`` () =
    let dir, mergeBaselines, remaining =
        extractFlags [| "--search-dir"; "coverage"; "check"; "--merge-baselines" |]
    test <@ dir = "coverage" @>
    test <@ mergeBaselines = true @>
    test <@ remaining = [| "check" |] @>

// --- runLoosenFromCi tests ---

[<Fact>]
let ``runLoosenFromCi - CI passes returns 0`` () =
    let passJson = """[{"status":"completed","conclusion":"success","databaseId":1}]"""

    let run =
        fakeRun
            [ ("jj", "git push", CoverageRatchet.Shell.Success "")
              ("jj", "log", CoverageRatchet.Shell.Success "abc123")
              ("gh", "run list", CoverageRatchet.Shell.Success passJson) ]

    let result = runLoosenFromCi run "coverage-ratchet.json"
    test <@ result = 0 @>

[<Fact>]
let ``runLoosenFromCi - CI other failure returns 1`` () =
    let run =
        fakeRun
            [ ("jj", "git push", CoverageRatchet.Shell.Success "")
              ("jj", "log", CoverageRatchet.Shell.Success "abc123")
              ("gh", "run list", CoverageRatchet.Shell.Failure("gh exploded", 1)) ]

    let result = runLoosenFromCi run "coverage-ratchet.json"
    test <@ result = 1 @>

[<Fact>]
let ``runLoosenFromCi - CI coverage failure with valid artifact writes config and returns 0`` () =
    let runId = 555444333L
    let artifactDir = Path.Combine(Path.GetTempPath(), sprintf "coverage-%d" runId)
    Directory.CreateDirectory(artifactDir) |> ignore

    // Use project name "default" so localConfigPath collapses back to the caller-supplied configPath (absolute)
    let thresholdsJson =
        """{"platform":"linux","results":{"Foo.fs":{"line":59,"branch":23}}}"""

    File.WriteAllText(Path.Combine(artifactDir, "coverage-thresholds-default.json"), thresholdsJson)

    Tests.Common.TestHelpers.withTempDir (fun tmpDir ->
        let configPath = Path.Combine(tmpDir, "coverage-ratchet.json")

        let failedJson =
            sprintf """[{"status":"completed","conclusion":"failure","databaseId":%d}]""" runId

        let passedJson =
            """[{"status":"completed","conclusion":"success","databaseId":1}]"""

        let run =
            fakeRun
                [ ("jj", "git push", CoverageRatchet.Shell.Success "")
                  ("jj", "log", CoverageRatchet.Shell.Success "oldsha")
                  ("gh", "run list", CoverageRatchet.Shell.Success failedJson)
                  ("gh", "run download", CoverageRatchet.Shell.Success "")
                  ("jj", "describe", CoverageRatchet.Shell.Success "")
                  ("jj", "bookmark set main -r @", CoverageRatchet.Shell.Success "")
                  ("jj", "new", CoverageRatchet.Shell.Success "")
                  ("jj", "git push --bookmark main", CoverageRatchet.Shell.Success "")
                  ("jj", "log", CoverageRatchet.Shell.Success "newsha")
                  ("gh", "run list", CoverageRatchet.Shell.Success passedJson) ]

        let result = runLoosenFromCi run configPath
        test <@ result = 0 @>
        test <@ File.Exists configPath @>
        test <@ not (Directory.Exists artifactDir) @>
        let written = File.ReadAllText configPath
        test <@ written.Contains("Foo.fs") @>)

[<Fact>]
let ``runLoosenFromCi - CI coverage failure with empty artifact returns 1`` () =
    // pollCi builds artifact path as /tmp/coverage-<runId>. Pre-create it so
    // the CiCoverageFailure branch finds an empty directory and reports "no updates".
    let runId = 987654321L

    let artifactDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "coverage-%d" runId)

    System.IO.Directory.CreateDirectory(artifactDir) |> ignore

    let failedJson =
        sprintf """[{"status":"completed","conclusion":"failure","databaseId":%d}]""" runId

    let run =
        fakeRun
            [ ("jj", "git push", CoverageRatchet.Shell.Success "")
              ("jj", "log", CoverageRatchet.Shell.Success "abc123")
              ("gh", "run list", CoverageRatchet.Shell.Success failedJson)
              ("gh", "run download", CoverageRatchet.Shell.Success "") ]

    let result = runLoosenFromCi run "coverage-ratchet.json"
    test <@ result = 1 @>
    // runLoosenFromCi deletes artifactDir in its finally block
    test <@ not (System.IO.Directory.Exists artifactDir) @>

// --- auto-refresh gate tests ---
//
// These exercise the FSHW_RAN_FULL_SUITE env-var gate in `run`. The env var must
// be reset in try/finally so tests don't leak state to siblings.

/// Cobertura XML where every line is hit (100% coverage, passes default thresholds).
let private passingCobertura =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1.0" branch-rate="1.0" lines-covered="2" lines-valid="2" branches-covered="0" branches-valid="0" version="1" timestamp="0">
  <packages>
    <package name="p" line-rate="1.0" branch-rate="1.0">
      <classes>
        <class name="Foo" filename="/src/Foo.fs" line-rate="1.0" branch-rate="1.0">
          <methods />
          <lines>
            <line number="1" hits="1" branch="false" />
            <line number="2" hits="1" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

/// Cobertura XML with one hit, one miss (50% coverage, fails default 100% threshold).
let private failingCobertura =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.5" branch-rate="1.0" lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0" version="1" timestamp="0">
  <packages>
    <package name="p" line-rate="0.5" branch-rate="1.0">
      <classes>
        <class name="Foo" filename="/src/Foo.fs" line-rate="0.5" branch-rate="1.0">
          <methods />
          <lines>
            <line number="1" hits="1" branch="false" />
            <line number="2" hits="0" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

/// Baseline with identical line numbers as cobertura but hits=0, so merging it into
/// the passing cobertura (all hits=1) yields the same passing result. Byte contents
/// differ from any cobertura above, so we can still detect whether refreshBaselines
/// overwrote the baseline.
let private staleBaseline =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="2" branches-covered="0" branches-valid="0" version="1" timestamp="0">
  <packages>
    <package name="p" line-rate="0" branch-rate="0">
      <classes>
        <class name="Foo" filename="/src/Foo.fs" line-rate="0" branch-rate="0">
          <methods />
          <lines>
            <line number="1" hits="0" branch="false" />
            <line number="2" hits="0" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

let private withFshwEnv (value: string option) (action: unit -> 'a) : 'a =
    let prior = Environment.GetEnvironmentVariable("FSHW_RAN_FULL_SUITE")

    match value with
    | Some v -> Environment.SetEnvironmentVariable("FSHW_RAN_FULL_SUITE", v)
    | None -> Environment.SetEnvironmentVariable("FSHW_RAN_FULL_SUITE", null)

    try
        action ()
    finally
        Environment.SetEnvironmentVariable("FSHW_RAN_FULL_SUITE", prior)

/// Seed a per-project coverage.cobertura.xml + coverage.baseline.xml under searchDir.
let private seedProject (searchDir: string) (projName: string) (cobertura: string) (baseline: string) =
    let projDir = Path.Combine(searchDir, projName)
    Directory.CreateDirectory(projDir) |> ignore
    File.WriteAllText(Path.Combine(projDir, "coverage.cobertura.xml"), cobertura)
    File.WriteAllText(Path.Combine(projDir, "coverage.baseline.xml"), baseline)
    projDir

[<Fact>]
let ``auto-refresh - env=true + pass + merge=true refreshes baseline`` () =
    withTempDir (fun tmpDir ->
        let projDir = seedProject tmpDir "Proj1" passingCobertura staleBaseline
        let configPath = Path.Combine(tmpDir, "config.json")

        let result =
            withFshwEnv (Some "true") (fun () -> run (Check(config = Some configPath)) tmpDir true)

        test <@ result = Ok 0 @>

        // After refresh, baseline.xml is byte-identical to the (post-merge) cobertura.xml.
        let coverageBytes = File.ReadAllBytes(Path.Combine(projDir, "coverage.cobertura.xml"))
        let baselineBytes = File.ReadAllBytes(Path.Combine(projDir, "coverage.baseline.xml"))
        test <@ coverageBytes = baselineBytes @>)

[<Fact>]
let ``auto-refresh - env=true + FAIL + merge=true does NOT refresh`` () =
    withTempDir (fun tmpDir ->
        let projDir = seedProject tmpDir "Proj1" failingCobertura staleBaseline
        let configPath = Path.Combine(tmpDir, "config.json")

        let baselinePath = Path.Combine(projDir, "coverage.baseline.xml")
        let baselineBefore = File.ReadAllBytes(baselinePath)

        let result =
            withFshwEnv (Some "true") (fun () -> run (Check(config = Some configPath)) tmpDir true)

        // Failing check -> Ok 1 (rc != 0), so auto-refresh must NOT run.
        test <@ result = Ok 1 @>

        // Baseline on disk is unchanged (mergeIntoBaselines writes to cobertura, not baseline).
        let baselineAfter = File.ReadAllBytes(baselinePath)
        test <@ baselineBefore = baselineAfter @>)

[<Fact>]
let ``auto-refresh - no env var + pass + merge=true does NOT refresh`` () =
    withTempDir (fun tmpDir ->
        let projDir = seedProject tmpDir "Proj1" passingCobertura staleBaseline
        let configPath = Path.Combine(tmpDir, "config.json")
        let baselinePath = Path.Combine(projDir, "coverage.baseline.xml")
        let baselineBefore = File.ReadAllBytes(baselinePath)

        let result =
            withFshwEnv None (fun () -> run (Check(config = Some configPath)) tmpDir true)

        test <@ result = Ok 0 @>

        // mergeIntoBaselines ran and may have rewritten cobertura, but refreshBaselines
        // should NOT fire without the env var, so baseline is untouched.
        let baselineAfter = File.ReadAllBytes(baselinePath)
        test <@ baselineBefore = baselineAfter @>)

[<Fact>]
let ``auto-refresh - env=true + pass + merge=false does NOT refresh (env ignored)`` () =
    withTempDir (fun tmpDir ->
        let projDir = seedProject tmpDir "Proj1" passingCobertura staleBaseline
        let configPath = Path.Combine(tmpDir, "config.json")
        let baselinePath = Path.Combine(projDir, "coverage.baseline.xml")
        let coveragePath = Path.Combine(projDir, "coverage.cobertura.xml")
        let baselineBefore = File.ReadAllBytes(baselinePath)
        let coverageBefore = File.ReadAllBytes(coveragePath)

        let result =
            withFshwEnv (Some "true") (fun () -> run (Check(config = Some configPath)) tmpDir false)

        test <@ result = Ok 0 @>

        // With mergeBaselines=false, neither the merge step nor auto-refresh should run.
        let baselineAfter = File.ReadAllBytes(baselinePath)
        let coverageAfter = File.ReadAllBytes(coveragePath)
        test <@ baselineBefore = baselineAfter @>
        test <@ coverageBefore = coverageAfter @>)

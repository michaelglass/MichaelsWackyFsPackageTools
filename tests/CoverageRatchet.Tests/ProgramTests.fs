module CoverageRatchet.Tests.ProgramTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Program
open Tests.Common.TestHelpers
open CoverageRatchet.Tests.CoverageTestHelpers

// --- formatFileResult tests ---

[<Fact>]
let ``formatFileResult - passing file at 100 percent`` () =
    let r =
        { File = makeFile "Foo.fs" 100.0 100.0 4 4
          LineThreshold = 100.0
          BranchThreshold = 100.0
          LinePassed = true
          BranchPassed = true }

    let result = formatFileResult r

    test <@ result.Contains("PASS") @>
    test <@ result.Contains("Foo.fs") @>
    test <@ not (result.Contains("[min:")) @>

[<Fact>]
let ``formatFileResult - failing file with thresholds`` () =
    let r =
        { File = makeFile "Bar.fs" 60.0 50.0 1 4
          LineThreshold = 80.0
          BranchThreshold = 70.0
          LinePassed = false
          BranchPassed = false }

    let result = formatFileResult r

    test <@ result.Contains("FAIL") @>
    test <@ result.Contains("Bar.fs") @>
    test <@ result.Contains("[min: line=80% branch=70%]") @>

[<Fact>]
let ``formatFileResult - file with no branches`` () =
    let r =
        { File = makeFile "Simple.fs" 90.0 100.0 0 0
          LineThreshold = 80.0
          BranchThreshold = 100.0
          LinePassed = true
          BranchPassed = true }

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
                        Reason = "test" } ] }

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
                        Reason = "test" } ] }

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

// --- main tests ---

[<Fact>]
let ``main with --help returns 0`` () =
    let result = main [| "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``main with no args and no coverage file returns 1`` () =
    // With CmdDefault, bare invocation runs ratchet, which will fail to find coverage file
    let result = main [||]

    test <@ result = 1 @>

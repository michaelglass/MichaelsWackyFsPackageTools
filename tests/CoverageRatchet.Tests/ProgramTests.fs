module CoverageRatchet.Tests.ProgramTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Program
open Tests.Common.TestHelpers

// --- parseArgs tests ---

[<Fact>]
let ``parseArgs - check command`` () =
    let cmd, configPath = parseArgs [| "check" |]

    test <@ cmd = Some "check" @>
    test <@ configPath = defaultConfigPath @>

[<Fact>]
let ``parseArgs - ratchet command`` () =
    let cmd, configPath = parseArgs [| "ratchet" |]

    test <@ cmd = Some "ratchet" @>
    test <@ configPath = defaultConfigPath @>

[<Fact>]
let ``parseArgs - config flag`` () =
    let cmd, configPath = parseArgs [| "check"; "--config"; "custom.json" |]

    test <@ cmd = Some "check" @>
    test <@ configPath = "custom.json" @>

[<Fact>]
let ``parseArgs - no args`` () =
    let cmd, configPath = parseArgs [||]

    test <@ cmd = None @>
    test <@ configPath = defaultConfigPath @>

[<Fact>]
let ``parseArgs - unknown args ignored`` () =
    let cmd, configPath = parseArgs [| "--verbose"; "check"; "--debug" |]

    test <@ cmd = Some "check" @>
    test <@ configPath = defaultConfigPath @>

// --- formatFileResult tests ---

let private makeFile name linePct branchPct branchesCovered branchesTotal =
    { FileName = name
      LinePct = linePct
      BranchPct = branchPct
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

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
        let result = run "check" (Path.Combine(tmpDir, "config.json")) tmpDir

        test <@ result = Error "No coverage.cobertura.xml found" @>)

[<Fact>]
let ``run - check with passing coverage file returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let result = run "check" (Path.Combine(tmpDir, "config.json")) tmpDir

        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with failing coverage returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let result = run "check" (Path.Combine(tmpDir, "config.json")) tmpDir

        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - ratchet creates config and returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 50)

        let configPath = Path.Combine(tmpDir, "config.json")
        let result = run "ratchet" configPath tmpDir

        test <@ result = Ok 0 @>
        test <@ File.Exists(configPath) @>)

[<Fact>]
let ``run - unknown command returns Error`` () =
    withTempDir (fun tmpDir ->
        let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")
        File.WriteAllText(xmlPath, makeCoverageXml 100)

        let result = run "badcmd" (Path.Combine(tmpDir, "config.json")) tmpDir

        test
            <@
                match result with
                | Error msg -> msg.Contains("Unknown command")
                | _ -> false
            @>)

// --- main tests ---

[<Fact>]
let ``main with no args returns 1`` () =
    let result = main [||]

    test <@ result = 1 @>

[<Fact>]
let ``main with bad command returns 1`` () =
    let result = main [| "badcmd" |]

    test <@ result = 1 @>

module CoverageRatchet.Core.Tests.ThresholdsTests

open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Core.Tests.TestHelpers

let private percentageFloor line branch =
    { Line = line
      Branch = branch
      Reason = None
      Platform = None }

// --- complete-report verdict (AUTOMATION-127) ---

[<Fact>]
let ``judge - missing percentage floor makes the run incomplete`` () =
    let config =
        { defaultsConfig with
            Overrides = Map.ofList [ "Missing.fs", percentageFloor 80.0 70.0 ] }

    match judge config [ makeFile "Measured.fs" 100.0 100.0 0 0 ] with
    | Incomplete(1, [ hole ]) ->
        test <@ hole.File = "Missing.fs" @>
        test <@ hole.HasPercentageFloor @>
        test <@ not hole.HasCountFloor @>
    | verdict -> failwithf "Expected one missing percentage floor, got %A" verdict

[<Fact>]
let ``judge - missing count-only floor makes the run incomplete`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Missing.fs", countFloor 10 2 ] }

    match judge config [ makeFile "Measured.fs" 100.0 100.0 0 0 ] with
    | Incomplete(_, [ hole ]) ->
        test <@ not hole.HasPercentageFloor @>
        test <@ hole.HasCountFloor @>
    | verdict -> failwithf "Expected one missing count floor, got %A" verdict

[<Fact>]
let ``judge - missing file with both floors is one obligation naming both kinds`` () =
    let config =
        { defaultsConfig with
            Overrides = Map.ofList [ "Missing.fs", percentageFloor 80.0 70.0 ]
            CountFloors = Map.ofList [ "Missing.fs", countFloor 10 2 ] }

    match judge config [ makeFile "Measured.fs" 100.0 100.0 0 0 ] with
    | Incomplete(_, [ hole ]) ->
        test <@ hole.File = "Missing.fs" @>
        test <@ hole.HasPercentageFloor && hole.HasCountFloor @>
    | verdict -> failwithf "Expected one combined missing-floor obligation, got %A" verdict

[<Fact>]
let ``judge - measured regression remains below-floor and carries missing floors`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Measured.fs", percentageFloor 90.0 90.0
                      "Missing.fs", percentageFloor 80.0 70.0 ] }

    let verdict = judge config [ makeFile "Measured.fs" 50.0 100.0 0 0 ]

    match verdict with
    | BelowFloor([ failure ], [], [ hole ]) ->
        test <@ failure.File.FileName = "Measured.fs" @>
        test <@ hole.File = "Missing.fs" @>
        test <@ exitCodeOf verdict = 1 @>
    | other -> failwithf "Expected regression plus missing floor, got %A" other

[<Fact>]
let ``judge - measured count regression is below-floor`` () =
    let config =
        { defaultsConfig with
            Overrides = Map.ofList [ "Foo.fs", percentageFloor 0.0 0.0 ]
            CountFloors = Map.ofList [ "Foo.fs", countFloor 8 0 ] }

    match judge config [ makeFileWithCounts "Foo.fs" 3 10 0 0 ] with
    | BelowFloor([], [ failure ], []) -> test <@ failure.File.FileName = "Foo.fs" @>
    | verdict -> failwithf "Expected a measured count-floor regression, got %A" verdict

[<Fact>]
let ``check - file meeting defaults passes`` () =
    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = check defaultsConfig files
    test <@ result = AllPassed @>

[<Fact>]
let ``check - file below defaults fails`` () =
    let files = [ makeFile "Foo.fs" 80.0 90.0 3 4 ]
    let result = check defaultsConfig files

    match result with
    | SomeFailed failed ->
        test <@ failed.Length = 1 @>
        test <@ failed.[0].File.FileName = "Foo.fs" @>
        test <@ not (FileResult.linePassed failed.[0]) @>
    | AllPassed -> failwith "Expected SomeFailed"

[<Fact>]
let ``check - file with override uses override thresholds`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy code"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 75.0 70.0 3 4 ]
    let result = check config files
    test <@ result = AllPassed @>

[<Fact>]
let ``check - empty file list returns AllPassed`` () =
    let result = check defaultsConfig []
    test <@ result = AllPassed @>

[<Fact>]
let ``loadConfig - missing file returns defaults`` () =
    let config = loadConfig "/nonexistent/path/config.json"
    test <@ config.DefaultLine = 100.0 @>
    test <@ config.DefaultBranch = 100.0 @>
    test <@ config.Overrides = Map.empty @>

[<Fact>]
let ``loadConfig - parses overrides correctly`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "reason": "legacy" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.Count = 1 @>
        test <@ config.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = Some "legacy" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveConfig roundtrips with loadConfig`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              CountFloors = Map.empty
              Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy code"
                        Platform = None } ] }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Reason = Some "legacy code" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``Platform.current returns a known platform`` () =
    let platform = Platform.current
    test <@ platform = MacOS || platform = Linux || platform = Windows @>

[<Fact>]
let ``Platform.ofString - valid inputs`` () =
    test <@ Platform.ofString "macos" = Some MacOS @>
    test <@ Platform.ofString "linux" = Some Linux @>
    test <@ Platform.ofString "windows" = Some Windows @>

[<Fact>]
let ``Platform.ofString - invalid input returns None`` () =
    test <@ Platform.ofString "nonexistent" = None @>
    test <@ Platform.ofString "" = None @>

[<Fact>]
let ``resolveConfig - platform-specific wins over all-platform`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawCountFloors = Map.empty
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 50.0
                      Branch = 50.0
                      Reason = Some "fallback"
                      Platform = None }
                    { Line = 84.0
                      Branch = 55.0
                      Reason = Some "specific"
                      Platform = Some Platform.current } ] ] }

    let config = resolveConfig raw
    test <@ config.Overrides.["Foo.fs"].Line = 84.0 @>
    test <@ config.Overrides.["Foo.fs"].Platform = Some Platform.current @>

[<Fact>]
let ``FileResult.passed - both pass`` () =
    let r =
        { File = makeFile "Foo.fs" 80.0 70.0 3 4
          LineThreshold = 80.0
          BranchThreshold = 70.0 }

    test <@ FileResult.passed r @>

[<Fact>]
let ``FileResult.passed - line fails`` () =
    let r =
        { File = makeFile "Foo.fs" 79.0 70.0 3 4
          LineThreshold = 80.0
          BranchThreshold = 70.0 }

    test <@ not (FileResult.passed r) @>

// --- count floors: absolute covered-line/branch floors (AUTOMATION-119) ---

[<Fact>]
let ``checkCounts - file below its covered-line floor FAILS`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 383 0 ] }

    let files = [ makeFileWithCounts "Foo.fs" 300 400 0 0 ]

    test <@ checkCounts config files <> CountsAllPassed @>

[<Fact>]
let ``checkCounts - file at its covered-line floor passes`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 383 0 ] }

    let files = [ makeFileWithCounts "Foo.fs" 383 400 0 0 ]

    test <@ checkCounts config files = CountsAllPassed @>

[<Fact>]
let ``checkCounts - file below its covered-BRANCH floor FAILS`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 0 55 ] }

    let files = [ makeFileWithCounts "Foo.fs" 100 100 44 62 ]

    test <@ checkCounts config files <> CountsAllPassed @>

[<Fact>]
let ``checkCounts - a file with no floor recorded is not count-checked`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Other.fs", countFloor 999 0 ] }

    let files = [ makeFileWithCounts "Foo.fs" 1 400 0 0 ]

    test <@ checkCounts config files = CountsAllPassed @>

[<Fact>]
let ``checkCounts - a shrinking denominator cannot fail a count floor`` () =
    // The regression ADR 0019 documents: the emitted-line set collapses from 400
    // to 90 between runs. Percentage swings wildly (75% -> 100%); the covered
    // count is unchanged, so the count floor holds steady.
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 90 0 ] }

    let wideDenominator = [ makeFileWithCounts "Foo.fs" 90 400 0 0 ]
    let narrowDenominator = [ makeFileWithCounts "Foo.fs" 90 90 0 0 ]

    test <@ checkCounts config wideDenominator = CountsAllPassed @>
    test <@ checkCounts config narrowDenominator = CountsAllPassed @>

    // Positive control: the same floor DOES fire when hits are genuinely lost.
    let realRegression = [ makeFileWithCounts "Foo.fs" 89 90 0 0 ]
    test <@ checkCounts config realRegression <> CountsAllPassed @>

// --- config representation: percentages and counts stay distinct ---

[<Fact>]
let ``loadRawConfig - a percentage-only config yields NO count floors`` () =
    let path = Path.GetTempFileName()

    try
        // 93 here means 93 PERCENT. It must never be read as 93 lines.
        File.WriteAllText(path, """{ "overrides": { "Foo.fs": { "line": 93, "branch": 80 } } }""")

        let raw = loadRawConfig path
        let config = resolveConfig raw

        test <@ config.Overrides.["Foo.fs"].Line = 93.0 @>
        test <@ config.CountFloors = Map.empty @>

        // A file at 5 covered lines passes, because no count floor exists to fail.
        test <@ checkCounts config [ makeFileWithCounts "Foo.fs" 5 5 0 0 ] = CountsAllPassed @>
    finally
        File.Delete(path)

[<Fact>]
let ``loadRawConfig - countFloors section is read as counts, not percentages`` () =
    let path = Path.GetTempFileName()

    try
        File.WriteAllText(
            path,
            """{
  "overrides": { "Foo.fs": { "line": 93, "branch": 80 } },
  "countFloors": { "Foo.fs": { "coveredLines": 93, "coveredBranches": 12 } }
}"""
        )

        let config = loadRawConfig path |> resolveConfig

        test <@ config.Overrides.["Foo.fs"].Line = 93.0 @>
        test <@ config.CountFloors.["Foo.fs"].CoveredLines = 93 @>
        test <@ config.CountFloors.["Foo.fs"].CoveredBranches = 12 @>

        // 93 as a COUNT: a file with 92 covered lines out of 92 is 100% and still fails.
        test
            <@
                checkCounts config [ makeFileWithCounts "Foo.fs" 92 92 12 12 ]
                <> CountsAllPassed
            @>

        test <@ checkCounts config [ makeFileWithCounts "Foo.fs" 93 400 12 12 ] = CountsAllPassed @>
    finally
        File.Delete(path)

[<Fact>]
let ``saveRawConfig - omits countFloors entirely when none are set`` () =
    let path = Path.GetTempFileName()

    try
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides =
                Map.ofList
                    [ "Foo.fs",
                      [ { Line = 93.0
                          Branch = 80.0
                          Reason = None
                          Platform = None } ] ]
              RawCountFloors = Map.empty }

        saveRawConfig path raw
        let written = File.ReadAllText(path)

        test <@ not (written.Contains("countFloors")) @>
    finally
        File.Delete(path)

[<Fact>]
let ``saveRawConfig - round-trips count floors`` () =
    let path = Path.GetTempFileName()

    try
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides = Map.empty
              RawCountFloors =
                Map.ofList
                    [ "Foo.fs",
                      [ { CoveredLines = 383
                          CoveredBranches = 41
                          Reason = Some "extracted to Shared.fs"
                          Platform = None } ] ] }

        saveRawConfig path raw
        let reloaded = loadRawConfig path

        test <@ reloaded.RawCountFloors = raw.RawCountFloors @>
    finally
        File.Delete(path)

[<Fact>]
let ``loadRawConfig - a countFloors entry may be a per-platform array`` () =
    let path = Path.GetTempFileName()

    try
        File.WriteAllText(
            path,
            sprintf
                """{
  "countFloors": {
    "Os.fs": [
      { "coveredLines": 100, "coveredBranches": 10, "platform": "%s" },
      { "coveredLines": 999, "coveredBranches": 99, "platform": "%s" }
    ]
  }
}"""
                (Platform.toString Platform.current)
                (Platform.toString otherPlatform)
        )

        let raw = loadRawConfig path
        test <@ raw.RawCountFloors.["Os.fs"].Length = 2 @>

        // Only the running platform's floor is enforced here — which is exactly why
        // a floor tagged for another platform is invisible to this machine's check.
        let config = resolveConfig raw
        test <@ config.CountFloors.["Os.fs"].CoveredLines = 100 @>
    finally
        File.Delete(path)

[<Fact>]
let ``resolveConfig - a count floor tagged only for another platform is not enforced`` () =
    let raw =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty
          RawCountFloors =
            Map.ofList
                [ "Os.fs",
                  [ { CoveredLines = 999
                      CoveredBranches = 99
                      Reason = None
                      Platform = Some otherPlatform } ] ] }

    let config = resolveConfig raw

    test <@ config.CountFloors = Map.empty @>
    test <@ checkCounts config [ makeFileWithCounts "Os.fs" 1 1 0 0 ] = CountsAllPassed @>

[<Fact>]
let ``saveRawConfig - a single platform-tagged count floor is written as an array`` () =
    let path = Path.GetTempFileName()

    try
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides = Map.empty
              RawCountFloors =
                Map.ofList
                    [ "Os.fs",
                      [ { CoveredLines = 100
                          CoveredBranches = 10
                          Reason = None
                          Platform = Some Platform.current } ] ] }

        saveRawConfig path raw
        let written = File.ReadAllText(path)

        test <@ written.Contains("[") @>
        test <@ written.Contains(Platform.toString Platform.current) @>

        let reloaded = loadRawConfig path
        test <@ reloaded.RawCountFloors = raw.RawCountFloors @>
    finally
        File.Delete(path)

[<Fact>]
let ``saveRawConfig - a file key with no entries serialises without crashing`` () =
    let path = Path.GetTempFileName()

    try
        // Degenerate but reachable shape: a filename mapped to an empty entry list.
        // Serialisation must not assume at least one entry exists.
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides = Map.ofList [ "Empty.fs", [] ]
              RawCountFloors = Map.ofList [ "AlsoEmpty.fs", [] ] }

        saveRawConfig path raw

        let reloaded = loadRawConfig path
        test <@ List.isEmpty reloaded.RawOverrides.["Empty.fs"] @>
        test <@ List.isEmpty reloaded.RawCountFloors.["AlsoEmpty.fs"] @>
    finally
        File.Delete(path)

// --- encoding stability (AUTOMATION-151) ---
//
// The floor file is the one file this project forbids anyone to hand-merge: floors are
// re-settled by a full run, never by picking a number out of a conflict. So a writer
// that rewrites bytes it was not asked to change does not merely make noise, it
// manufactures the exact conflict the policy says must not be resolved by hand.
//
// Note what is NOT sufficient evidence here. save -> load -> save is a fixed point even
// with the DEFAULT encoder, because both writes escape identically; a bare idempotence
// test passes on the broken writer. The defect only shows against text a human typed,
// so the assertion that has to hold is that a literal em-dash SURVIVES a write.

/// The realistic case, taken from the reasons actually in this repo's floor files: an
/// em-dash, an apostrophe and a backtick, every one of which `JavaScriptEncoder.Default`
/// rewrites into a `\uXXXX` escape.
let private proseReason =
    "settled to the CI-measured actual \u2014 `httpGet`'s live call is not covered"

[<Fact>]
let ``saveRawConfig - a reason's non-ASCII text survives the write as literal UTF-8`` () =
    let path = Path.GetTempFileName()

    try
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides =
                Map.ofList
                    [ "Api.fs",
                      [ { Line = 93.0
                          Branch = 84.0
                          Reason = Some proseReason
                          Platform = None } ] ]
              RawCountFloors = Map.empty }

        saveRawConfig path raw
        let written = File.ReadAllText(path)

        // Positive control: the reason really is in this file, so the assertions below
        // are about text that IS there rather than text that was never written.
        test <@ written.Contains("Api.fs") @>

        test <@ written.Contains(proseReason) @>
        test <@ not (written.Contains("\\u2014")) @>
        test <@ not (written.Contains("\\u0027")) @>
        test <@ not (written.Contains("\\u0060")) @>

        // The value survives too, not just the bytes: a literal encoding that failed to
        // round-trip would be a different bug wearing this one's fix.
        let reloaded = loadRawConfig path
        let reloadedReason = (reloaded.RawOverrides.["Api.fs"] |> List.head).Reason
        test <@ reloadedReason = Some proseReason @>
    finally
        File.Delete(path)

[<Fact>]
let ``saveRawConfig - rewriting a config that changed no floor produces the same bytes`` () =
    let path = Path.GetTempFileName()

    try
        let raw =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides =
                Map.ofList
                    [ "Api.fs",
                      [ { Line = 93.0
                          Branch = 84.0
                          Reason = Some proseReason
                          Platform = None } ] ]
              RawCountFloors =
                Map.ofList
                    [ "Api.fs",
                      [ { CoveredLines = 383
                          CoveredBranches = 41
                          Reason = Some proseReason
                          Platform = None } ] ] }

        saveRawConfig path raw
        let firstWrite = File.ReadAllBytes(path)

        // What the ratchet does on a run that moves nothing: read the file back, write
        // it out again. The bytes are the contract, because the bytes are what jj diffs.
        saveRawConfig path (loadRawConfig path)
        let secondWrite = File.ReadAllBytes(path)

        test <@ firstWrite.Length > 0 @>
        test <@ secondWrite = firstWrite @>
    finally
        File.Delete(path)

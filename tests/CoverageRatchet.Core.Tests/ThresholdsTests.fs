module CoverageRatchet.Core.Tests.ThresholdsTests

open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Core.Tests.TestHelpers

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

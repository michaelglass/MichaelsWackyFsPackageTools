module CoverageRatchet.Tests.ThresholdsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Tests.CoverageTestHelpers

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
        test <@ not failed.[0].LinePassed @>
        test <@ not failed.[0].BranchPassed @>
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
                        Reason = "legacy code"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 75.0 70.0 3 4 ]
    let result = check config files

    test <@ result = AllPassed @>

[<Fact>]
let ``check - multiple files mixed pass and fail`` () =
    let files =
        [ makeFile "Good.fs" 100.0 100.0 2 2
          makeFile "Bad.fs" 50.0 40.0 1 3
          makeFile "AlsoGood.fs" 100.0 100.0 0 0 ]

    let result = check defaultsConfig files

    match result with
    | SomeFailed failed ->
        test <@ failed.Length = 1 @>
        test <@ failed.[0].File.FileName = "Bad.fs" @>
    | AllPassed -> failwith "Expected SomeFailed"

[<Fact>]
let ``loadConfig - missing file returns defaults`` () =
    let config = loadConfig "/nonexistent/path/config.json"

    test <@ config.DefaultLine = 100.0 @>
    test <@ config.DefaultBranch = 100.0 @>
    test <@ config.Overrides = Map.empty @>

[<Fact>]
let ``loadConfig - empty object returns defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "{}")
        let config = loadConfig tmpFile

        test <@ config.DefaultLine = 100.0 @>
        test <@ config.DefaultBranch = 100.0 @>
        test <@ config.Overrides = Map.empty @>
    finally
        File.Delete(tmpFile)

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
        test <@ config.Overrides.["Foo.fs"].Reason = "legacy" @>
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
                        Reason = "legacy code"
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 50.0
                        Reason = "new module"
                        Platform = None } ] }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.Overrides.Count = 2 @>
        test <@ loaded.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Reason = "legacy code" @>
        test <@ loaded.Overrides.["Bar.fs"].Line = 80.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Branch = 50.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Reason = "new module" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - whitespace-only content returns defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "   \n  \t  \n  ")
        let config = loadConfig tmpFile

        test <@ config.DefaultLine = 100.0 @>
        test <@ config.DefaultBranch = 100.0 @>
        test <@ config.Overrides = Map.empty @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - override missing line field defaults to 100`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "branch": 65, "reason": "no line" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 100.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "no line" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - override missing branch field defaults to 100`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "reason": "no branch" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 100.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "no branch" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - override missing reason field defaults to empty`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65 } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - overrides key with empty object returns defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{ "overrides": {} }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.DefaultLine = 100.0 @>
        test <@ config.DefaultBranch = 100.0 @>
        test <@ config.Overrides = Map.empty @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - no overrides key returns defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{"version": 1}"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.DefaultLine = 100.0 @>
        test <@ config.DefaultBranch = 100.0 @>
        test <@ config.Overrides = Map.empty @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - null reason defaults to empty string`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "reason": null } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Reason = "" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveConfig with empty overrides roundtrips`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let config =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              Overrides = Map.empty }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.DefaultLine = 100.0 @>
        test <@ loaded.DefaultBranch = 100.0 @>
        test <@ loaded.Overrides = Map.empty @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``currentPlatform returns a known platform string`` () =
    let platform = CoverageRatchet.Thresholds.currentPlatform
    test <@ platform = "macos" || platform = "linux" || platform = "windows" @>

[<Fact>]
let ``loadConfig - array override with platform filters to current platform`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            sprintf
                """{ "overrides": { "Foo.fs": [
                    { "line": 84, "branch": 55, "reason": "native", "platform": "%s" },
                    { "line": 0, "branch": 0, "reason": "not here", "platform": "nonexistent" }
                ] } }"""
                currentPlatform

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.Count = 1 @>
        test <@ config.Overrides.["Foo.fs"].Line = 84.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 55.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "native" @>
        test <@ config.Overrides.["Foo.fs"].Platform = Some currentPlatform @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - array override with no matching platform uses defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": [
                { "line": 0, "branch": 0, "reason": "not here", "platform": "nonexistent" }
            ] } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.ContainsKey("Foo.fs") = false @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - single object override still works backward compatible`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "reason": "legacy" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.Count = 1 @>
        test <@ config.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "legacy" @>
        test <@ config.Overrides.["Foo.fs"].Platform = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - platform-specific override wins over all-platform`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            sprintf
                """{ "overrides": { "Foo.fs": [
                    { "line": 50, "branch": 50, "reason": "fallback" },
                    { "line": 84, "branch": 55, "reason": "specific", "platform": "%s" }
                ] } }"""
                currentPlatform

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 84.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 55.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = "specific" @>
        test <@ config.Overrides.["Foo.fs"].Platform = Some currentPlatform @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveRawConfig roundtrips platform overrides`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let raw: RawConfig =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides =
                Map.ofList
                    [ "Foo.fs",
                      [ { Line = 84.0
                          Branch = 55.0
                          Reason = "native"
                          Platform = Some "macos" }
                        { Line = 0.0
                          Branch = 0.0
                          Reason = "not here"
                          Platform = Some "linux" }
                        { Line = 50.0
                          Branch = 50.0
                          Reason = "fallback"
                          Platform = None } ] ] }

        saveRawConfig tmpFile raw
        let loaded = loadRawConfig tmpFile

        test <@ loaded.RawOverrides.["Foo.fs"].Length = 3 @>

        let macos =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = Some "macos")

        test <@ macos.Line = 84.0 @>
        test <@ macos.Branch = 55.0 @>
        test <@ macos.Reason = "native" @>

        let linux =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = Some "linux")

        test <@ linux.Line = 0.0 @>
        test <@ linux.Reason = "not here" @>

        let fallback =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = None)

        test <@ fallback.Line = 50.0 @>
        test <@ fallback.Reason = "fallback" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveRawConfig writes single all-platform override as object`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let raw: RawConfig =
            { DefaultLine = 100.0
              DefaultBranch = 100.0
              RawOverrides =
                Map.ofList
                    [ "Foo.fs",
                      [ { Line = 70.0
                          Branch = 65.0
                          Reason = "legacy"
                          Platform = None } ] ] }

        saveRawConfig tmpFile raw
        let json = File.ReadAllText(tmpFile)

        let valueKind, lineVal =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let fooEl = doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs")
            fooEl.ValueKind, fooEl.GetProperty("line").GetDouble()

        test <@ valueKind = System.Text.Json.JsonValueKind.Object @>
        test <@ lineVal = 70.0 @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveConfig still roundtrips`` () =
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
                        Reason = "legacy code"
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 50.0
                        Reason = "new module"
                        Platform = None } ] }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.Overrides.Count = 2 @>
        test <@ loaded.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Reason = "legacy code" @>
        test <@ loaded.Overrides.["Bar.fs"].Line = 80.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Branch = 50.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Reason = "new module" @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadRawConfig preserves all platform entries`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            sprintf
                """{ "overrides": { "Foo.fs": [
                    { "line": 84, "branch": 55, "reason": "native", "platform": "%s" },
                    { "line": 0, "branch": 0, "reason": "not here", "platform": "nonexistent" },
                    { "line": 50, "branch": 50, "reason": "fallback" }
                ] } }"""
                currentPlatform

        File.WriteAllText(tmpFile, json)
        let raw = loadRawConfig tmpFile

        test <@ raw.RawOverrides.["Foo.fs"].Length = 3 @>
    finally
        File.Delete(tmpFile)

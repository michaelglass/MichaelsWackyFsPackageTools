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
        test <@ not (FileResult.linePassed failed.[0]) @>
        test <@ not (FileResult.branchPassed failed.[0]) @>
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
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 50.0
                        Reason = Some "new module"
                        Platform = None } ] }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.Overrides.Count = 2 @>
        test <@ loaded.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Reason = Some "legacy code" @>
        test <@ loaded.Overrides.["Bar.fs"].Line = 80.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Branch = 50.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Reason = Some "new module" @>
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
        test <@ config.Overrides.["Foo.fs"].Reason = Some "no line" @>
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
        test <@ config.Overrides.["Foo.fs"].Reason = Some "no branch" @>
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
        test <@ config.Overrides.["Foo.fs"].Reason = None @>
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

        test <@ config.Overrides.["Foo.fs"].Reason = None @>
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
let ``Platform.current returns a known platform`` () =
    let platform = Platform.current
    test <@ platform = MacOS || platform = Linux || platform = Windows @>

[<Fact>]
let ``loadConfig - array override with platform filters to current platform`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            sprintf
                """{ "overrides": { "Foo.fs": [
                    { "line": 84, "branch": 55, "reason": "native", "platform": "%s" },
                    { "line": 0, "branch": 0, "reason": "not here", "platform": "%s" }
                ] } }"""
                (Platform.toString Platform.current)
                (Platform.toString otherPlatform)

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.Count = 1 @>
        test <@ config.Overrides.["Foo.fs"].Line = 84.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 55.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = Some "native" @>
        test <@ config.Overrides.["Foo.fs"].Platform = Some Platform.current @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - array override with no matching platform uses defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            sprintf
                """{ "overrides": { "Foo.fs": [
                { "line": 0, "branch": 0, "reason": "not here", "platform": "%s" }
            ] } }"""
                (Platform.toString otherPlatform)

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
        test <@ config.Overrides.["Foo.fs"].Reason = Some "legacy" @>
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
                (Platform.toString Platform.current)

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 84.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 55.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = Some "specific" @>
        test <@ config.Overrides.["Foo.fs"].Platform = Some Platform.current @>
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
                          Reason = Some "native"
                          Platform = Some MacOS }
                        { Line = 0.0
                          Branch = 0.0
                          Reason = Some "not here"
                          Platform = Some Linux }
                        { Line = 50.0
                          Branch = 50.0
                          Reason = Some "fallback"
                          Platform = None } ] ] }

        saveRawConfig tmpFile raw
        let loaded = loadRawConfig tmpFile

        test <@ loaded.RawOverrides.["Foo.fs"].Length = 3 @>

        let macos =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = Some MacOS)

        test <@ macos.Line = 84.0 @>
        test <@ macos.Branch = 55.0 @>
        test <@ macos.Reason = Some "native" @>

        let linux =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = Some Linux)

        test <@ linux.Line = 0.0 @>
        test <@ linux.Reason = Some "not here" @>

        let fallback =
            loaded.RawOverrides.["Foo.fs"] |> List.find (fun o -> o.Platform = None)

        test <@ fallback.Line = 50.0 @>
        test <@ fallback.Reason = Some "fallback" @>
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
                          Reason = Some "legacy"
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
                        Reason = Some "legacy code"
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 50.0
                        Reason = Some "new module"
                        Platform = None } ] }

        saveConfig tmpFile config
        let loaded = loadConfig tmpFile

        test <@ loaded.Overrides.Count = 2 @>
        test <@ loaded.Overrides.["Foo.fs"].Line = 70.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Branch = 65.0 @>
        test <@ loaded.Overrides.["Foo.fs"].Reason = Some "legacy code" @>
        test <@ loaded.Overrides.["Bar.fs"].Line = 80.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Branch = 50.0 @>
        test <@ loaded.Overrides.["Bar.fs"].Reason = Some "new module" @>
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
                    { "line": 0, "branch": 0, "reason": "not here", "platform": "%s" },
                    { "line": 50, "branch": 50, "reason": "fallback" }
                ] } }"""
                (Platform.toString Platform.current)
                (Platform.toString otherPlatform)

        File.WriteAllText(tmpFile, json)
        let raw = loadRawConfig tmpFile

        test <@ raw.RawOverrides.["Foo.fs"].Length = 3 @>
    finally
        File.Delete(tmpFile)

// --- Platform module tests ---

[<Fact>]
let ``Platform.ofString - valid inputs`` () =
    test <@ Platform.ofString "macos" = Some MacOS @>
    test <@ Platform.ofString "linux" = Some Linux @>
    test <@ Platform.ofString "windows" = Some Windows @>

[<Fact>]
let ``Platform.ofString - case insensitive`` () =
    test <@ Platform.ofString "MacOS" = Some MacOS @>
    test <@ Platform.ofString "LINUX" = Some Linux @>
    test <@ Platform.ofString "Windows" = Some Windows @>

[<Fact>]
let ``Platform.ofString - invalid input returns None`` () =
    test <@ Platform.ofString "nonexistent" = None @>
    test <@ Platform.ofString "" = None @>
    test <@ Platform.ofString "osx" = None @>

[<Fact>]
let ``Platform.toString roundtrips with ofString`` () =
    test <@ Platform.toString MacOS |> Platform.ofString = Some MacOS @>
    test <@ Platform.toString Linux |> Platform.ofString = Some Linux @>
    test <@ Platform.toString Windows |> Platform.ofString = Some Windows @>

// --- FileResult module tests ---

[<Fact>]
let ``FileResult.linePassed - at threshold`` () =
    let r =
        { File = makeFile "Foo.fs" 80.0 100.0 0 0
          LineThreshold = 80.0
          BranchThreshold = 100.0 }

    test <@ FileResult.linePassed r @>

[<Fact>]
let ``FileResult.linePassed - below threshold`` () =
    let r =
        { File = makeFile "Foo.fs" 79.9 100.0 0 0
          LineThreshold = 80.0
          BranchThreshold = 100.0 }

    test <@ not (FileResult.linePassed r) @>

[<Fact>]
let ``FileResult.branchPassed - at threshold`` () =
    let r =
        { File = makeFile "Foo.fs" 100.0 70.0 3 4
          LineThreshold = 100.0
          BranchThreshold = 70.0 }

    test <@ FileResult.branchPassed r @>

[<Fact>]
let ``FileResult.branchPassed - below threshold`` () =
    let r =
        { File = makeFile "Foo.fs" 100.0 69.9 3 4
          LineThreshold = 100.0
          BranchThreshold = 70.0 }

    test <@ not (FileResult.branchPassed r) @>

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

[<Fact>]
let ``FileResult.passed - branch fails`` () =
    let r =
        { File = makeFile "Foo.fs" 80.0 69.0 3 4
          LineThreshold = 80.0
          BranchThreshold = 70.0 }

    test <@ not (FileResult.passed r) @>

// --- Override.Reason serialization tests ---

[<Fact>]
let ``saveConfig - Reason None omits reason key`` () =
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
                        Reason = None
                        Platform = None } ] }

        saveConfig tmpFile config
        let json = File.ReadAllText(tmpFile)

        let hasReason =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs").TryGetProperty("reason")
            |> fst

        test <@ not hasReason @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveConfig - Reason Some includes reason key`` () =
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
                        Reason = Some "test reason"
                        Platform = None } ] }

        saveConfig tmpFile config
        let json = File.ReadAllText(tmpFile)

        let reason =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs").GetProperty("reason").GetString()

        test <@ reason = "test reason" @>
    finally
        File.Delete(tmpFile)

// --- loadConfig roundtrip tests ---

[<Fact>]
let ``loadConfig roundtrip through saveRawConfig and loadRawConfig`` () =
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
                          Reason = Some "test"
                          Platform = None } ] ] }

        saveRawConfig tmpFile raw
        let loaded = loadRawConfig tmpFile

        test <@ loaded.RawOverrides.["Foo.fs"].Length = 1 @>
        test <@ loaded.RawOverrides.["Foo.fs"].[0].Line = 70.0 @>
        test <@ loaded.RawOverrides.["Foo.fs"].[0].Reason = Some "test" @>
    finally
        File.Delete(tmpFile)

// --- buildFileResults tests ---

[<Fact>]
let ``buildFileResults - empty file list`` () =
    let results = buildFileResults defaultsConfig []
    test <@ results = [] @>

// --- resolveConfig tests ---

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
let ``resolveConfig - all-platform used when no platform match`` () =
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
                      Reason = Some "other"
                      Platform = Some otherPlatform } ] ] }

    let config = resolveConfig raw
    test <@ config.Overrides.["Foo.fs"].Line = 50.0 @>
    test <@ config.Overrides.["Foo.fs"].Platform = None @>

[<Fact>]
let ``resolveConfig - no match returns no override`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 84.0
                      Branch = 55.0
                      Reason = Some "other"
                      Platform = Some otherPlatform } ] ] }

    let config = resolveConfig raw
    test <@ config.Overrides.ContainsKey("Foo.fs") = false @>

// --- parseOverrideElement edge cases ---

[<Fact>]
let ``loadConfig - null reason parsed as None`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "reason": null } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Reason = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - empty reason parsed as None`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "reason": "" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Reason = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - missing reason parsed as None`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65 } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Reason = None @>
    finally
        File.Delete(tmpFile)

// --- saveRawConfig single vs array tests ---

[<Fact>]
let ``saveRawConfig - single all-platform override as object`` () =
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
                          Reason = Some "legacy"
                          Platform = None } ] ] }

        saveRawConfig tmpFile raw
        let json = File.ReadAllText(tmpFile)

        let valueKind =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs").ValueKind

        test <@ valueKind = System.Text.Json.JsonValueKind.Object @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``saveRawConfig - platform override as array`` () =
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
                          Reason = Some "macos"
                          Platform = Some MacOS }
                        { Line = 80.0
                          Branch = 75.0
                          Reason = Some "linux"
                          Platform = Some Linux } ] ] }

        saveRawConfig tmpFile raw
        let json = File.ReadAllText(tmpFile)

        let valueKind, arrayLen =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let fooEl = doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs")
            fooEl.ValueKind, fooEl.GetArrayLength()

        test <@ valueKind = System.Text.Json.JsonValueKind.Array @>
        test <@ arrayLen = 2 @>
    finally
        File.Delete(tmpFile)

// --- parseOverrideElement edge cases for platform ---

[<Fact>]
let ``loadConfig - null platform parsed as None`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "platform": null } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Platform = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - invalid platform string parsed as None and becomes all-platform`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "platform": "bsd" } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        // "bsd" is not a valid platform, so Platform = None, which acts as all-platform
        test <@ config.Overrides.ContainsKey("Foo.fs") @>
        test <@ config.Overrides.["Foo.fs"].Platform = None @>
        test <@ config.Overrides.["Foo.fs"].Line = 70.0 @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadRawConfig - invalid platform string preserved as None in raw`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json =
            """{ "overrides": { "Foo.fs": { "line": 70, "branch": 65, "platform": "bsd" } } }"""

        File.WriteAllText(tmpFile, json)
        let raw = loadRawConfig tmpFile

        test <@ raw.RawOverrides.["Foo.fs"].Length = 1 @>
        test <@ raw.RawOverrides.["Foo.fs"].[0].Platform = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - override with only line and branch uses default reason and platform`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{ "overrides": { "Foo.fs": { "line": 50, "branch": 40 } } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 50.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 40.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = None @>
        test <@ config.Overrides.["Foo.fs"].Platform = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``loadConfig - override with empty object uses all defaults`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let json = """{ "overrides": { "Foo.fs": {} } }"""

        File.WriteAllText(tmpFile, json)
        let config = loadConfig tmpFile

        test <@ config.Overrides.["Foo.fs"].Line = 100.0 @>
        test <@ config.Overrides.["Foo.fs"].Branch = 100.0 @>
        test <@ config.Overrides.["Foo.fs"].Reason = None @>
        test <@ config.Overrides.["Foo.fs"].Platform = None @>
    finally
        File.Delete(tmpFile)

// --- saveConfig with platform override ---

[<Fact>]
let ``saveConfig - platform override includes platform key`` () =
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
                        Reason = None
                        Platform = Some MacOS } ] }

        saveConfig tmpFile config
        let json = File.ReadAllText(tmpFile)

        let valueKind, hasPlatform, platformVal =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            let fooEl = doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs")
            let entry = fooEl.[0]
            let hasP = entry.TryGetProperty("platform") |> fst
            let pVal = entry.GetProperty("platform").GetString()
            fooEl.ValueKind, hasP, pVal

        // Single entry with platform is written as array
        test <@ valueKind = System.Text.Json.JsonValueKind.Array @>
        test <@ hasPlatform @>
        test <@ platformVal = "macos" @>
    finally
        File.Delete(tmpFile)

// --- buildFileResults with override ---

[<Fact>]
let ``buildFileResults uses override thresholds when available`` () =
    let config =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          Overrides =
            Map.ofList
                [ "Foo.fs",
                  { Line = 70.0
                    Branch = 60.0
                    Reason = Some "legacy"
                    Platform = None } ] }

    let files =
        [ makeFile "Foo.fs" 80.0 75.0 3 4
          makeFile "Bar.fs" 95.0 90.0 2 3 ]

    let results = buildFileResults config files

    let fooResult = results |> List.find (fun r -> r.File.FileName = "Foo.fs")
    let barResult = results |> List.find (fun r -> r.File.FileName = "Bar.fs")

    test <@ fooResult.LineThreshold = 70.0 @>
    test <@ fooResult.BranchThreshold = 60.0 @>
    test <@ barResult.LineThreshold = 100.0 @>
    test <@ barResult.BranchThreshold = 100.0 @>

// --- check with all passing ---

[<Fact>]
let ``check - all files passing returns AllPassed`` () =
    let config =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          Overrides =
            Map.ofList
                [ "Foo.fs",
                  { Line = 70.0
                    Branch = 60.0
                    Reason = Some "legacy"
                    Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = check config files

    test <@ result = AllPassed @>

// --- check with empty files ---

[<Fact>]
let ``check - empty file list returns AllPassed`` () =
    let result = check defaultsConfig []

    test <@ result = AllPassed @>

// --- saveRawConfig with single platform-specific entry ---

[<Fact>]
let ``saveRawConfig - single platform-specific override written as array`` () =
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
                          Reason = Some "native"
                          Platform = Some MacOS } ] ] }

        saveRawConfig tmpFile raw
        let json = File.ReadAllText(tmpFile)

        let valueKind =
            use doc = System.Text.Json.JsonDocument.Parse(json)
            doc.RootElement.GetProperty("overrides").GetProperty("Foo.fs").ValueKind

        // Single entry WITH platform is written as array (not object)
        test <@ valueKind = System.Text.Json.JsonValueKind.Array @>
    finally
        File.Delete(tmpFile)

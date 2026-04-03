module CoverageRatchet.Tests.ThresholdsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let private makeFile name linePct branchPct branchesCovered branchesTotal =
    { FileName = name
      LinePct = linePct
      BranchPct = branchPct
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

let private defaultsConfig =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      Overrides = Map.empty }

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
                        Reason = "legacy code" } ] }

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
                        Reason = "legacy code" }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 50.0
                        Reason = "new module" } ] }

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

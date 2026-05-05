module CoverageRatchet.Core.Tests.RatchetTests

open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Core.Tests.TestHelpers

[<Fact>]
let ``ratchet tightens override when coverage improves`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 75.0 @>

[<Fact>]
let ``ratchet floors fractional coverage to integer thresholds`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.3 75.7 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 75.0 @>

[<Fact>]
let ``ratchet removes override when file reaches defaults`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 95.0
                        Reason = Some "almost there"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchet config files

    test <@ result.Overrides.ContainsKey("Foo.fs") = false @>

[<Fact>]
let ``ratchet never lowers thresholds`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 70.0 @>

[<Fact>]
let ``ratchetWithStatus returns NoChanges when all thresholds met and unchanged`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.0 70.0 3 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | NoChanges -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus returns Tightened when coverage improved`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | Tightened _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus returns Failed when coverage dropped below threshold`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``loosen sets thresholds to actual coverage`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = Some "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosen config files

    test <@ result.Overrides.["Foo.fs"].Line = 70.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 60.0 @>

[<Fact>]
let ``loosen adds override for file below 100 percent with no existing override`` () =
    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.ContainsKey("New.fs") @>
    test <@ result.Overrides.["New.fs"].Line = 80.0 @>
    test <@ result.Overrides.["New.fs"].Reason = Some "loosened automatically" @>

[<Fact>]
let ``loosen removes override for file at 100 percent`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = Some "was low"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = loosen config files

    test <@ result.Overrides.ContainsKey("Foo.fs") = false @>

[<Fact>]
let ``ratchetRaw updates non-platform entry when no platform-specific entries exist`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 50.0
                      Branch = 40.0
                      Reason = Some "legacy"
                      Platform = None } ] ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 3 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]

    test <@ entries.Length = 1 @>
    test <@ entries.[0].Line = 70.0 @>
    test <@ entries.[0].Branch = 60.0 @>
    test <@ entries.[0].Platform = None @>

[<Fact>]
let ``loosenRaw adds platform-agnostic entry for new file`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosenRaw raw files
    test <@ result.RawOverrides.ContainsKey("New.fs") @>
    let entries = result.RawOverrides.["New.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = None @>
    test <@ entries.[0].Line = 80.0 @>

[<Fact>]
let ``parseCiThresholds - parses minimal JSON format`` () =
    let json = """{"platform":"linux","results":{"Foo.fs":{"line":59,"branch":23}}}"""

    let platform, results = parseCiThresholds json
    test <@ platform = Linux @>
    test <@ results.["Foo.fs"] = { Line = 59.0; Branch = 23.0 } @>

[<Fact>]
let ``parseCiThresholds - empty string raises actionable error`` () =
    let ex = Assert.ThrowsAny<exn>(fun () -> parseCiThresholds "" |> ignore)
    test <@ ex.Message.Contains("empty") @>

[<Fact>]
let ``mergeFromCi - adds new file override when CI has file below defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "NewFile.fs", { Line = 80.0; Branch = 60.0 } ]
    let result = mergeFromCi raw Linux ciResults
    test <@ result.RawOverrides.ContainsKey("NewFile.fs") @>
    let entries = result.RawOverrides.["NewFile.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some Linux @>
    test <@ entries.[0].Line = 80.0 @>

[<Fact>]
let ``mergeFromCi - skips files at or above defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "Perfect.fs", { Line = 100.0; Branch = 100.0 } ]
    let result = mergeFromCi raw Linux ciResults
    test <@ result.RawOverrides.ContainsKey("Perfect.fs") = false @>

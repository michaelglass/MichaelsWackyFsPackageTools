module CoverageRatchet.Tests.RatchetTests

open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Tests.CoverageTestHelpers

[<Fact>]
let ``ratchet tightens override when coverage improves`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
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
                        Reason = "almost there"
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
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 70.0 @>

[<Fact>]
let ``ratchet preserves reason text`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = "complex legacy module"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Reason = "complex legacy module" @>

[<Fact>]
let ``ratchet handles files not in overrides`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 75.0 70.0 3 4; makeFile "Bar.fs" 90.0 85.0 2 3 ]

    let result = ratchet config files

    // Foo.fs override should be tightened
    test <@ result.Overrides.["Foo.fs"].Line = 75.0 @>
    // Bar.fs should NOT get a new override (it wasn't in overrides before)
    test <@ result.Overrides.ContainsKey("Bar.fs") = false @>

[<Fact>]
let ``ratchet keeps override unchanged when file not in coverage data`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Missing.fs",
                      { Line = 60.0
                        Branch = 50.0
                        Reason = "file removed or not covered"
                        Platform = None } ] }

    // Coverage data has no entry for Missing.fs
    let files = [ makeFile "Other.fs" 100.0 100.0 4 4 ]
    let result = ratchet config files

    test <@ result.Overrides.ContainsKey("Missing.fs") @>
    test <@ result.Overrides.["Missing.fs"].Line = 60.0 @>
    test <@ result.Overrides.["Missing.fs"].Branch = 50.0 @>
    test <@ result.Overrides.["Missing.fs"].Reason = "file removed or not covered" @>

[<Fact>]
let ``loosen sets thresholds to actual coverage`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosen config files

    test <@ result.Overrides.["Foo.fs"].Line = 70.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 60.0 @>

[<Fact>]
let ``loosen preserves existing reason`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = "CLI entry point"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosen config files

    test <@ result.Overrides.["Foo.fs"].Reason = "CLI entry point" @>

[<Fact>]
let ``loosen adds override for file below 100 percent with no existing override`` () =
    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.ContainsKey("New.fs") @>
    test <@ result.Overrides.["New.fs"].Line = 80.0 @>
    test <@ result.Overrides.["New.fs"].Branch = 75.0 @>
    test <@ result.Overrides.["New.fs"].Reason = "loosened automatically" @>

[<Fact>]
let ``loosen removes override for file at 100 percent`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = "was low"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = loosen config files

    test <@ result.Overrides.ContainsKey("Foo.fs") = false @>

[<Fact>]
let ``loosen keeps override for file not in coverage data`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Missing.fs",
                      { Line = 60.0
                        Branch = 50.0
                        Reason = "not in report"
                        Platform = None } ] }

    let files = [ makeFile "Other.fs" 100.0 100.0 4 4 ]
    let result = loosen config files

    test <@ result.Overrides.ContainsKey("Missing.fs") @>
    test <@ result.Overrides.["Missing.fs"].Line = 60.0 @>
    test <@ result.Overrides.["Missing.fs"].Branch = 50.0 @>
    test <@ result.Overrides.["Missing.fs"].Reason = "not in report" @>

[<Fact>]
let ``ratchetWithStatus returns NoChanges when all thresholds met and unchanged`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 80.0 70.0 3 4 ]
    let result = ratchetWithStatus config files

    test
        <@
            match result with
            | NoChanges _ -> true
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
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]
    let result = ratchetWithStatus config files

    test
        <@
            match result with
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
                        Reason = "legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]
    let result = ratchetWithStatus config files

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus returns Failed even if some files improved and others dropped`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = "legacy"
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = "also legacy"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4; makeFile "Bar.fs" 60.0 50.0 1 4 ]
    let result = ratchetWithStatus config files

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus - file with no override below 100 percent is Failed`` () =
    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = ratchetWithStatus defaultsConfig files

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetRaw preserves entries for other platforms`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 70.0
                      Branch = 60.0
                      Reason = "this platform"
                      Platform = Some currentPlatform }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = "other"
                      Platform = Some "nonexistent" } ] ] }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    let mine = entries |> List.find (fun o -> o.Platform = Some currentPlatform)
    let other = entries |> List.find (fun o -> o.Platform = Some "nonexistent")
    test <@ mine.Line = 80.0 @>
    test <@ mine.Branch = 75.0 @>
    test <@ other.Line = 0.0 @>
    test <@ other.Branch = 0.0 @>

[<Fact>]
let ``ratchetRaw removes current-platform entry when it reaches defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 90.0
                      Branch = 95.0
                      Reason = "almost"
                      Platform = Some currentPlatform }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = "other"
                      Platform = Some "nonexistent" } ] ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    // Current platform entry removed, other kept
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some "nonexistent" @>

[<Fact>]
let ``loosenRaw preserves entries for other platforms`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 90.0
                      Branch = 85.0
                      Reason = "this platform"
                      Platform = Some currentPlatform }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = "other"
                      Platform = Some "nonexistent" } ] ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    let mine = entries |> List.find (fun o -> o.Platform = Some currentPlatform)
    let other = entries |> List.find (fun o -> o.Platform = Some "nonexistent")
    test <@ mine.Line = 70.0 @>
    test <@ other.Line = 0.0 @>

[<Fact>]
let ``loosenRaw adds platform-specific entry for new file`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosenRaw raw files
    test <@ result.RawOverrides.ContainsKey("New.fs") @>
    let entries = result.RawOverrides.["New.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some currentPlatform @>
    test <@ entries.[0].Line = 80.0 @>

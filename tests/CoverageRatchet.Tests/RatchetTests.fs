module CoverageRatchet.Tests.RatchetTests

open Xunit
open Swensen.Unquote
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet

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
let ``ratchet tightens override when coverage improves`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = "legacy" } ] }

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
                        Reason = "almost there" } ] }

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
                        Reason = "legacy" } ] }

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
                        Reason = "complex legacy module" } ] }

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
                        Reason = "legacy" } ] }

    let files = [ makeFile "Foo.fs" 75.0 70.0 3 4; makeFile "Bar.fs" 90.0 85.0 2 3 ]

    let result = ratchet config files

    // Foo.fs override should be tightened
    test <@ result.Overrides.["Foo.fs"].Line = 75.0 @>
    // Bar.fs should NOT get a new override (it wasn't in overrides before)
    test <@ result.Overrides.ContainsKey("Bar.fs") = false @>

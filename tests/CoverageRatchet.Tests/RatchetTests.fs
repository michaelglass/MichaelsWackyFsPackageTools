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
let ``ratchet preserves reason text`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "complex legacy module"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Reason = Some "complex legacy module" @>

[<Fact>]
let ``ratchet handles files not in overrides`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 70.0
                        Branch = 65.0
                        Reason = Some "legacy"
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
                        Reason = Some "file removed or not covered"
                        Platform = None } ] }

    // Coverage data has no entry for Missing.fs
    let files = [ makeFile "Other.fs" 100.0 100.0 4 4 ]
    let result = ratchet config files

    test <@ result.Overrides.ContainsKey("Missing.fs") @>
    test <@ result.Overrides.["Missing.fs"].Line = 60.0 @>
    test <@ result.Overrides.["Missing.fs"].Branch = 50.0 @>
    test <@ result.Overrides.["Missing.fs"].Reason = Some "file removed or not covered" @>

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
let ``loosen preserves existing reason`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Foo.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = Some "CLI entry point"
                        Platform = None } ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosen config files

    test <@ result.Overrides.["Foo.fs"].Reason = Some "CLI entry point" @>

[<Fact>]
let ``loosen adds override for file below 100 percent with no existing override`` () =
    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.ContainsKey("New.fs") @>
    test <@ result.Overrides.["New.fs"].Line = 80.0 @>
    test <@ result.Overrides.["New.fs"].Branch = 75.0 @>
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
let ``loosen keeps override for file not in coverage data`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Missing.fs",
                      { Line = 60.0
                        Branch = 50.0
                        Reason = Some "not in report"
                        Platform = None } ] }

    let files = [ makeFile "Other.fs" 100.0 100.0 4 4 ]
    let result = loosen config files

    test <@ result.Overrides.ContainsKey("Missing.fs") @>
    test <@ result.Overrides.["Missing.fs"].Line = 60.0 @>
    test <@ result.Overrides.["Missing.fs"].Branch = 50.0 @>
    test <@ result.Overrides.["Missing.fs"].Reason = Some "not in report" @>

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
    let result = ratchetWithStatus config files

    test
        <@
            match result with
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
                        Reason = Some "legacy"
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
                        Reason = Some "legacy"
                        Platform = None }
                      "Bar.fs",
                      { Line = 80.0
                        Branch = 70.0
                        Reason = Some "also legacy"
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
                      Reason = Some "this platform"
                      Platform = Some Platform.current }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = Some "other"
                      Platform = Some Windows } ] ] }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    let mine = entries |> List.find (fun o -> o.Platform = Some Platform.current)
    let other = entries |> List.find (fun o -> o.Platform = Some Windows)
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
                      Reason = Some "almost"
                      Platform = Some Platform.current }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = Some "other"
                      Platform = Some Windows } ] ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    // Current platform entry removed, other kept
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some Windows @>

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
                      Reason = Some "this platform"
                      Platform = Some Platform.current }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = Some "other"
                      Platform = Some Windows } ] ] }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    let mine = entries |> List.find (fun o -> o.Platform = Some Platform.current)
    let other = entries |> List.find (fun o -> o.Platform = Some Windows)
    test <@ mine.Line = 70.0 @>
    test <@ other.Line = 0.0 @>

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
let ``mergeFromCi - adds ci-platform entry splitting existing non-platform override`` () =
    let ciPlatform = if Platform.current = MacOS then Linux else Windows

    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Program.fs",
                  [ { Line = 49.0
                      Branch = 31.0
                      Reason = Some "legacy"
                      Platform = None } ] ] }

    let ciResults = Map.ofList [ "Program.fs", { Line = 59.0; Branch = 23.0 } ]
    let result = mergeFromCi raw ciPlatform ciResults
    let entries = result.RawOverrides.["Program.fs"]
    test <@ entries.Length = 2 @>
    let localEntry = entries |> List.find (fun o -> o.Platform = Some Platform.current)
    let ciEntry = entries |> List.find (fun o -> o.Platform = Some ciPlatform)
    test <@ localEntry.Line = 49.0 @>
    test <@ localEntry.Branch = 31.0 @>
    test <@ ciEntry.Line = 59.0 @>
    test <@ ciEntry.Branch = 23.0 @>

[<Fact>]
let ``mergeFromCi - updates existing linux platform entry`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Program.fs",
                  [ { Line = 49.0
                      Branch = 31.0
                      Reason = Some "legacy"
                      Platform = Some MacOS }
                    { Line = 55.0
                      Branch = 20.0
                      Reason = Some "ci"
                      Platform = Some Linux } ] ] }

    let ciResults = Map.ofList [ "Program.fs", { Line = 59.0; Branch = 23.0 } ]
    let result = mergeFromCi raw Linux ciResults
    let entries = result.RawOverrides.["Program.fs"]
    test <@ entries.Length = 2 @>
    let macosEntry = entries |> List.find (fun o -> o.Platform = Some MacOS)
    let linuxEntry = entries |> List.find (fun o -> o.Platform = Some Linux)
    test <@ macosEntry.Line = 49.0 @>
    test <@ macosEntry.Branch = 31.0 @>
    test <@ linuxEntry.Line = 59.0 @>
    test <@ linuxEntry.Branch = 23.0 @>

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
    test <@ entries.[0].Branch = 60.0 @>

[<Fact>]
let ``mergeFromCi - skips files at or above defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "Perfect.fs", { Line = 100.0; Branch = 100.0 } ]
    let result = mergeFromCi raw Linux ciResults
    test <@ result.RawOverrides.ContainsKey("Perfect.fs") = false @>

[<Fact>]
let ``parseCiThresholds - parses minimal JSON format`` () =
    let json =
        """{"platform":"linux","results":{"Foo.fs":{"line":59,"branch":23},"Bar.fs":{"line":81,"branch":66}}}"""

    let platform, results = parseCiThresholds json
    test <@ platform = Linux @>
    test <@ results.Count = 2 @>
    test <@ results.["Foo.fs"] = { Line = 59.0; Branch = 23.0 } @>
    test <@ results.["Bar.fs"] = { Line = 81.0; Branch = 66.0 } @>

// --- RatchetStatus.NoChanges tests ---

[<Fact>]
let ``ratchetRawWithStatus returns NoChanges when thresholds unchanged`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 80.0
                      Branch = 70.0
                      Reason = Some "legacy"
                      Platform = None } ] ] }

    let files = [ makeFile "Foo.fs" 80.0 70.0 3 4 ]
    let result = ratchetRawWithStatus raw files
    test <@ result = NoChanges @>

[<Fact>]
let ``ratchetRawWithStatus returns Tightened when coverage improved`` () =
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

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]
    let result = ratchetRawWithStatus raw files

    test
        <@
            match result with
            | Tightened _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetRawWithStatus returns Failed when coverage dropped`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 80.0
                      Branch = 70.0
                      Reason = Some "legacy"
                      Platform = None } ] ] }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]
    let result = ratchetRawWithStatus raw files

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

// --- mergeFromCi additional branches ---

[<Fact>]
let ``mergeFromCi - adds new platform entry to existing platform entries`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Program.fs",
                  [ { Line = 49.0
                      Branch = 31.0
                      Reason = Some "local"
                      Platform = Some MacOS } ] ] }

    let ciResults = Map.ofList [ "Program.fs", { Line = 59.0; Branch = 23.0 } ]
    let result = mergeFromCi raw Linux ciResults
    let entries = result.RawOverrides.["Program.fs"]
    test <@ entries.Length = 2 @>
    let linuxEntry = entries |> List.find (fun o -> o.Platform = Some Linux)
    test <@ linuxEntry.Line = 59.0 @>
    test <@ linuxEntry.Branch = 23.0 @>

// --- parseCiThresholds with Platform ---

[<Fact>]
let ``parseCiThresholds - macos platform`` () =
    let json = """{"platform":"macos","results":{"Foo.fs":{"line":90,"branch":80}}}"""

    let platform, results = parseCiThresholds json
    test <@ platform = MacOS @>
    test <@ results.["Foo.fs"] = { Line = 90.0; Branch = 80.0 } @>

[<Fact>]
let ``parseCiThresholds - windows platform`` () =
    let json = """{"platform":"windows","results":{"Foo.fs":{"line":90,"branch":80}}}"""

    let platform, results = parseCiThresholds json
    test <@ platform = Windows @>

[<Fact>]
let ``parseCiThresholds - unknown platform defaults to current`` () =
    let json = """{"platform":"unknown","results":{"Foo.fs":{"line":90,"branch":80}}}"""

    let platform, _results = parseCiThresholds json
    test <@ platform = Platform.current @>

// --- mergeRawOverrides branches ---

[<Fact>]
let ``ratchetRaw removes entry entirely when all platforms reach defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 90.0
                      Branch = 95.0
                      Reason = Some "almost"
                      Platform = None } ] ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchetRaw raw files

    test <@ result.RawOverrides.ContainsKey("Foo.fs") = false @>

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

// --- loosenRaw branches ---

[<Fact>]
let ``loosenRaw removes current-platform entry when file reaches defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 90.0
                      Branch = 95.0
                      Reason = Some "this platform"
                      Platform = Some Platform.current }
                    { Line = 0.0
                      Branch = 0.0
                      Reason = Some "other"
                      Platform = Some otherPlatform } ] ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]

    // Current platform entry removed, other kept
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some otherPlatform @>

[<Fact>]
let ``loosenRaw adds new file with platform-agnostic entry`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let files = [ makeFile "Brand.fs" 60.0 50.0 1 4 ]
    let result = loosenRaw raw files

    test <@ result.RawOverrides.ContainsKey("Brand.fs") @>
    let entries = result.RawOverrides.["Brand.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = None @>
    test <@ entries.[0].Line = 60.0 @>
    test <@ entries.[0].Branch = 50.0 @>

[<Fact>]
let ``loosenRaw preserves other-platform-only entry when adding agnostic entry for same file`` () =
    // File has only an other-platform entry. Loosen adds a platform-agnostic entry alongside it.
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Shell.fs",
                  [ { Line = 0.0
                      Branch = 0.0
                      Reason = Some "process execution"
                      Platform = Some otherPlatform } ] ] }

    let files = [ makeFile "Shell.fs" 60.0 50.0 1 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Shell.fs"]

    test <@ entries.Length = 2 @>
    let other = entries |> List.find (fun o -> o.Platform = Some otherPlatform)
    let agnostic = entries |> List.find (fun o -> o.Platform = None)
    test <@ other.Line = 0.0 @>
    test <@ agnostic.Line = 60.0 @>

[<Fact>]
let ``loosenRaw preserves other-platform-only entry when current platform meets defaults`` () =
    // File has only a linux entry. On macOS, file is at 100% — no macOS entry needed,
    // but the linux entry must survive.
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Shell.fs",
                  [ { Line = 0.0
                      Branch = 0.0
                      Reason = Some "process execution"
                      Platform = Some otherPlatform } ] ] }

    let files = [ makeFile "Shell.fs" 100.0 100.0 4 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Shell.fs"]

    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some otherPlatform @>
    test <@ entries.[0].Line = 0.0 @>

[<Fact>]
let ``loosenRaw updates agnostic entry in place`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Thresholds.fs",
                  [ { Line = 90.0
                      Branch = 85.0
                      Reason = Some "compiler branches"
                      Platform = None } ] ] }

    let files = [ makeFile "Thresholds.fs" 95.0 92.0 3 4 ]
    let result = loosenRaw raw files
    let entries = result.RawOverrides.["Thresholds.fs"]

    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = None @>
    test <@ entries.[0].Line = 95.0 @>
    test <@ entries.[0].Branch = 92.0 @>

[<Fact>]
let ``loosenRaw removes agnostic entry when file reaches defaults`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Thresholds.fs",
                  [ { Line = 90.0
                      Branch = 85.0
                      Reason = Some "compiler branches"
                      Platform = None } ] ] }

    let files = [ makeFile "Thresholds.fs" 100.0 100.0 4 4 ]
    let result = loosenRaw raw files

    test <@ not (result.RawOverrides.ContainsKey("Thresholds.fs")) @>

// --- mergeFromCi additional branches ---

[<Fact>]
let ``mergeFromCi - skips files at defaults even with existing entries`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Existing.fs",
                  [ { Line = 80.0
                      Branch = 70.0
                      Reason = Some "local"
                      Platform = Some Platform.current } ] ] }

    // CI says this file is at 100/100 -- should not add a CI entry
    let ciResults = Map.ofList [ "Existing.fs", { Line = 100.0; Branch = 100.0 } ]
    let result = mergeFromCi raw otherPlatform ciResults

    // Existing entry unchanged, no CI entry added
    let entries = result.RawOverrides.["Existing.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some Platform.current @>

[<Fact>]
let ``mergeFromCi - adds entry to file with no prior overrides`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "New.fs", { Line = 70.0; Branch = 55.0 } ]
    let result = mergeFromCi raw otherPlatform ciResults

    test <@ result.RawOverrides.ContainsKey("New.fs") @>
    let entries = result.RawOverrides.["New.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some otherPlatform @>
    test <@ entries.[0].Line = 70.0 @>

[<Fact>]
let ``mergeFromCi - line below default but branch at default still adds entry`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "Half.fs", { Line = 50.0; Branch = 100.0 } ]
    let result = mergeFromCi raw otherPlatform ciResults

    test <@ result.RawOverrides.ContainsKey("Half.fs") @>

[<Fact>]
let ``mergeFromCi - branch below default but line at default still adds entry`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let ciResults = Map.ofList [ "Half.fs", { Line = 100.0; Branch = 50.0 } ]
    let result = mergeFromCi raw otherPlatform ciResults

    test <@ result.RawOverrides.ContainsKey("Half.fs") @>

// --- ratchetRawWithStatus additional branches ---

[<Fact>]
let ``ratchetRawWithStatus returns Tightened when override removed entirely`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 90.0
                      Branch = 95.0
                      Reason = Some "almost"
                      Platform = None } ] ] }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchetRawWithStatus raw files

    test
        <@
            match result with
            | Tightened newRaw -> not (newRaw.RawOverrides.ContainsKey("Foo.fs"))
            | _ -> false
        @>

// --- ratchetRaw with non-platform entry fallback ---

[<Fact>]
let ``ratchetRaw updates non-platform entry when platform-specific exists for current platform`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 50.0
                      Branch = 40.0
                      Reason = Some "all"
                      Platform = None }
                    { Line = 60.0
                      Branch = 50.0
                      Reason = Some "current"
                      Platform = Some Platform.current } ] ] }

    let files = [ makeFile "Foo.fs" 80.0 70.0 3 4 ]
    let result = ratchetRaw raw files
    let entries = result.RawOverrides.["Foo.fs"]
    // The platform-specific entry should be updated, the non-platform one unchanged
    let currentEntry =
        entries |> List.find (fun o -> o.Platform = Some Platform.current)

    let allEntry = entries |> List.find (fun o -> o.Platform = None)
    test <@ currentEntry.Line = 80.0 @>
    test <@ currentEntry.Branch = 70.0 @>
    test <@ allEntry.Line = 50.0 @>
    test <@ allEntry.Branch = 40.0 @>

// --- ratchetRawWithStatus Failed includes file names ---

[<Fact>]
let ``ratchetRawWithStatus Failed includes failed file names`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let files = [ makeFile "Low.fs" 50.0 40.0 1 4 ]
    let result = ratchetRawWithStatus raw files

    test
        <@
            match result with
            | Failed(_, failedFiles) -> failedFiles = [ "Low.fs" ]
            | _ -> false
        @>

// --- loosen with file already at defaults does not create override ---

[<Fact>]
let ``loosen does not create override for file at 100 percent with no existing override`` () =
    let files = [ makeFile "Perfect.fs" 100.0 100.0 4 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.ContainsKey("Perfect.fs") = false @>

// --- loosen with existing override and new file ---

[<Fact>]
let ``loosen updates existing and adds new overrides`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [ "Existing.fs",
                      { Line = 90.0
                        Branch = 85.0
                        Reason = Some "was high"
                        Platform = None } ] }

    let files =
        [ makeFile "Existing.fs" 70.0 60.0 2 4; makeFile "New.fs" 80.0 75.0 3 4 ]

    let result = loosen config files

    test <@ result.Overrides.["Existing.fs"].Line = 70.0 @>
    test <@ result.Overrides.["Existing.fs"].Branch = 60.0 @>
    test <@ result.Overrides.["New.fs"].Line = 80.0 @>
    test <@ result.Overrides.["New.fs"].Branch = 75.0 @>

// --- loosen floors fractional values ---

[<Fact>]
let ``loosen floors fractional coverage`` () =
    let files = [ makeFile "Frac.fs" 80.9 75.7 3 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.["Frac.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Frac.fs"].Branch = 75.0 @>

// --- mergeFromCi with existing non-platform entry splits to platform entries ---

[<Fact>]
let ``mergeFromCi splits non-platform entry into platform entries when CI below defaults`` () =
    let ciPlatform = otherPlatform

    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 70.0
                      Branch = 60.0
                      Reason = Some "shared"
                      Platform = None } ] ] }

    let ciResults = Map.ofList [ "Foo.fs", { Line = 80.0; Branch = 50.0 } ]
    let result = mergeFromCi raw ciPlatform ciResults
    let entries = result.RawOverrides.["Foo.fs"]

    // Non-platform entry should be promoted to current platform
    test <@ entries.Length = 2 @>
    let local = entries |> List.find (fun o -> o.Platform = Some Platform.current)
    let ci = entries |> List.find (fun o -> o.Platform = Some ciPlatform)
    test <@ local.Line = 70.0 @>
    test <@ ci.Line = 80.0 @>
    test <@ ci.Branch = 50.0 @>

// --- ratchetRaw with new file not in existing overrides ---

[<Fact>]
let ``ratchetRaw does not add new entries for files not in overrides`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides = Map.empty }

    let files = [ makeFile "NewFile.fs" 100.0 100.0 4 4 ]
    let result = ratchetRaw raw files

    test <@ result.RawOverrides.ContainsKey("NewFile.fs") = false @>

// --- ratchetWithStatus with empty files ---

[<Fact>]
let ``ratchetWithStatus with no files and no overrides returns NoChanges`` () =
    let result = ratchetWithStatus defaultsConfig []

    test
        <@
            match result with
            | NoChanges -> true
            | _ -> false
        @>

// --- mergeFromCi with empty CI results ---

[<Fact>]
let ``mergeFromCi with empty CI results leaves raw unchanged`` () =
    let raw: RawConfig =
        { DefaultLine = 100.0
          DefaultBranch = 100.0
          RawOverrides =
            Map.ofList
                [ "Foo.fs",
                  [ { Line = 70.0
                      Branch = 60.0
                      Reason = Some "test"
                      Platform = None } ] ] }

    let ciResults = Map.empty
    let result = mergeFromCi raw otherPlatform ciResults
    test <@ result.RawOverrides = raw.RawOverrides @>

// --- parseCiThresholds with empty results ---

[<Fact>]
let ``parseCiThresholds with empty results returns empty map`` () =
    let json = """{"platform":"linux","results":{}}"""
    let platform, results = parseCiThresholds json
    test <@ platform = Linux @>
    test <@ results = Map.empty @>

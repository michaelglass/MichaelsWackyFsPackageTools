module FsSemanticTagger.Tests.DeclaredBumpTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Version
open FsSemanticTagger.Api
open FsSemanticTagger.DeclaredBump
open FsSemanticTagger.Release

// markerLevel: which changelog lines declare what

[<Theory>]
[<InlineData("- feat!: drop the v1 schema")>]
[<InlineData("- fix!: reject old databases")>]
[<InlineData("- feat(core)!: rename the table")>]
[<InlineData("- **feat!:** bold entry")>]
[<InlineData("  - fix!: a nested bullet")>]
[<InlineData("* refactor!: star bullet")>]
[<InlineData("feat!: no bullet at all")>]
[<InlineData("BREAKING CHANGE: SchemaVersion is now 10")>]
[<InlineData("- BREAKING-CHANGE: hyphenated footer form")>]
let ``a breaking marker declares a breaking change`` (line: string) =
    test <@ markerLevel line = Some DeclaresBreaking @>

[<Theory>]
[<InlineData("- feat: add a flag")>]
[<InlineData("- feat(cli): scoped")>]
[<InlineData("+ FEAT: upper-case type")>]
let ``a feat marker declares a feature`` (line: string) =
    test <@ markerLevel line = Some DeclaresFeature @>

[<Theory>]
[<InlineData("- fix: handle a null")>]
[<InlineData("- chore: tidy")>]
[<InlineData("- build(deps): bump X from 1 to 2")>]
let ``a non-breaking non-feat marker declares a patch`` (line: string) =
    test <@ markerLevel line = Some DeclaresPatch @>

[<Theory>]
[<InlineData("- test entry")>]
[<InlineData("")>]
[<InlineData("- note!: not a conventional type")>]
[<InlineData("- prose that mentions feat!: midway")>]
[<InlineData("- Breaking change: prose, not the footer token")>]
[<InlineData("  - **Breaking (API):** a sub-bullet heading")>]
[<InlineData("> ### A callout")>]
let ``a line without a conventional marker declares nothing`` (line: string) = test <@ markerLevel line = None @>

// declare: one changelog's strongest declaration

[<Fact>]
let ``declare keeps the strongest marker and the first entry that declared it`` () =
    let lines = [ "- fix: a"; "- feat!: b"; "- feat: c"; "- fix!: d" ]

    test
        <@
            declare "CHANGELOG.md" lines = Some
                { Level = DeclaresBreaking
                  Source = "CHANGELOG.md"
                  Entry = "- feat!: b" }
        @>

[<Fact>]
let ``declare ignores markers inside fenced code`` () =
    let lines =
        [ "- fix: document the syntax"
          "```"
          "- feat!: an example, not an entry"
          "```"
          "~~~"
          "feat: also an example"
          "~~~" ]

    test <@ (declare "c" lines |> Option.map _.Level) = Some DeclaresPatch @>

[<Fact>]
let ``declare returns None when no line carries a marker`` () =
    test <@ declare "c" [ "- test entry"; "prose" ] = None @>
    test <@ declare "c" [] = None @>

// strongest: across every changelog behind one tag

[<Fact>]
let ``strongest picks the strongest declaration, and the first on a tie`` () =
    let d level source =
        { Level = level
          Source = source
          Entry = "- e" }

    test <@ strongest [] = None @>

    test
        <@
            strongest
                [ d DeclaresPatch "a"
                  d DeclaresBreaking "b"
                  d DeclaresFeature "c"
                  d DeclaresBreaking "d" ] = Some(d DeclaresBreaking "b")
        @>

// floor: declared intent is a lower bound, and every disagreement is reported

let private declared level =
    Some
        { Level = level
          Source = "src/TestPrune.Core/CHANGELOG.md"
          Entry = "- feat!: SchemaVersion 9 -> 10" }

let private breaking = Breaking(ApiSignature "  Foo::Bar(): String", [])
let private addition = Addition(ApiSignature "  Foo::Baz(): String", [])

[<Fact>]
let ``with nothing declared the computed change passes through silently`` () =
    for change in [ breaking; addition; NoChange ] do
        test <@ floor change None = (change, None) @>

[<Fact>]
let ``a declared breaking change floors an unchanged API at breaking, and says so`` () =
    let change, report = floor NoChange (declared DeclaresBreaking)

    test
        <@
            match change with
            | Breaking _ -> true
            | _ -> false
        @>

    let report = report |> Option.defaultValue ""
    test <@ report.Contains "declares a breaking change" @>
    test <@ report.Contains "found no public API change" @>
    test <@ report.Contains "src/TestPrune.Core/CHANGELOG.md" @>
    test <@ report.Contains "- feat!: SchemaVersion 9 -> 10" @>

[<Fact>]
let ``a declared feature floors an unchanged API at an addition`` () =
    let change, report = floor NoChange (declared DeclaresFeature)

    test
        <@
            match change with
            | Addition _ -> true
            | _ -> false
        @>

    test <@ report |> Option.exists _.Contains("declares a feature") @>

[<Fact>]
let ``a declared fix with an unchanged API stays a patch and reports nothing (positive control)`` () =
    test <@ floor NoChange (declared DeclaresPatch) = (NoChange, None) @>

[<Fact>]
let ``agreement between declared and computed reports nothing`` () =
    test <@ floor breaking (declared DeclaresBreaking) = (breaking, None) @>
    test <@ floor addition (declared DeclaresFeature) = (addition, None) @>

[<Fact>]
let ``a computed change stronger than declared keeps the computed change, and is reported`` () =
    let change, report = floor breaking (declared DeclaresFeature)
    test <@ change = breaking @>
    let report = report |> Option.defaultValue ""
    test <@ report.Contains "API diff found a breaking change" @>
    test <@ report.Contains "Foo::Bar(): String" @>
    test <@ report.Contains "declares only a feature" @>

    let change, report = floor addition (declared DeclaresPatch)
    test <@ change = addition @>
    test <@ report |> Option.exists _.Contains("declares only a patch-level change") @>

// Replaying the release that motivated this: TestPrune.Core, last released at 7.0.0,
// changed // `[<Literal>] SchemaVersion` 9 -> 10. A literal is inlined at every use site and
// never appears in the public-API dump, so the diff said NoChange and the tagger
// planned a patch, 7.0.1, while the changelog said `feat!:`.

[<Fact>]
let ``replay: a declared breaking change with an invisible API change plans 8.0.0, not a patch`` () =
    let current = parse "7.0.0"

    test <@ determineBump current NoChange = parse "7.0.1" @>

    let change, _ = floor NoChange (declared DeclaresBreaking)
    test <@ determineBump current change = parse "8.0.0" @>

[<Fact>]
let ``a declared breaking change before 1.0 floors at a minor bump`` () =
    let change, _ = floor NoChange (declared DeclaresBreaking)
    test <@ determineBump (parse "0.3.2") change = parse "0.4.0" @>

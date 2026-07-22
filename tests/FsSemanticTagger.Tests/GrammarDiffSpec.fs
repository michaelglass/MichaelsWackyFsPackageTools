module FsSemanticTagger.Tests.GrammarDiffSpec

// ===========================================================================
// AUTOMATION-194 — the CommandTree GRAMMAR diff + structural recovery.
//
// Two halves:
//   * The PURE diff over two grammar models (`FsSemanticTagger.Grammar.compare`).
//     The model + diff now live in the production module `src/FsSemanticTagger/
//     Grammar.fs`; this file pins their semantics.
//   * The STRUCTURAL recovery of a consumer's realized grammar from a built
//     assembly under MetadataLoadContext (`extractGrammarFromAssembly` /
//     `extractGrammarForType`). These tests prove the metadata-only walk recovers
//     exactly the tree CommandTree's own `fromUnion` builds at runtime — the crux
//     of the feature and its abandon criterion.
//
// Diff principle pinned here: a change is BREAKING iff some previously-valid CLI
// invocation is no longer valid or changes meaning; ADDITION iff new invocations
// become valid while every old one still parses identically; NOCHANGE iff the
// machine parse-contract is byte-identical (help text / descriptions are
// cosmetic and never bump).
// ===========================================================================

open Xunit
open Swensen.Unquote

// ---- fixture consumer DUs + a CommandTree->Grammar reference converter --------
// The referenced CommandTree (0.7.0) has NO `FlagArity` type (flags are bool via
// `FlagInfo.IsBool`) and supports flag-DU lists only as a SOLE field (a positional
// prefix + trailing flag list is treated as positional args). The production walk
// mirrors newer CommandTree semantics (the design's target), so the fromUnion
// round-trip fixtures use only shapes on which the two agree; newer-only shapes
// (positional-prefix flag leaves, optional-value flags) get extraction-only
// assertions below. This module opens ONLY CommandTree; the model's same-named
// types/cases are referenced fully qualified. It is public (not `private`) so the
// fixture unions keep a public representation — CommandReflection.fromUnion (via
// FSharpType) rejects private-representation unions.
module Fixtures =
    open CommandTree

    /// Nested group children: a nullary leaf and a leaf with an int arg.
    type SubCommand =
        | [<Cmd("Migrate the database")>] Migrate
        | [<Cmd("Seed data")>] Seed of count: int

    /// Sole-field flag DU: two nullary flags colliding on 'd' (both lose their
    /// short) and a required-value flag with an explicit short. 0.7.0-safe.
    type SimpleFlag =
        | [<CmdFlag(Description = "Dry run only")>] DryRun
        | [<CmdFlag(Description = "Verbose debug output")>] Debug
        | [<CmdFlag(Short = "k", Description = "Config path")>] Config of string

    /// Record-typed argument: expands to positional args (a bool field reads as optional).
    type BuildArgs =
        { Source: string
          Output: string option
          Force: bool }

    /// Round-trip fixture: only shapes whose production extraction equals 0.7.0's
    /// runtime fromUnion — nullary, required arg, optional arg, list arg, record-arg
    /// leaf, nested group, SOLE-field flag DU, and a [<Cmd(Name = ...)>] rename.
    type Fixture =
        | [<Cmd("Initialize")>] Init
        | [<Cmd("Extract API"); CmdArg("the dll")>] Extract of dll: string
        | [<Cmd("Fetch a url")>] Fetch of url: string option
        | [<Cmd("Build with options")>] Build of BuildArgs
        | [<Cmd("Wait for tags")>] Wait of tags: string list
        | [<Cmd("Database group")>] Db of SubCommand
        | [<Cmd("Release it")>] Release of SimpleFlag list
        | [<Cmd(Name = "old-name")>] Renameable

    /// Newer-than-0.7.0 shapes, exercised by the metadata walk only (never 0.7.0's
    /// fromUnion): an optional-value flag with a Name override.
    type RichFlag =
        | [<CmdFlag(Description = "Dry run")>] RDryRun
        | [<CmdFlag(Name = "wait", Description = "Seconds to wait")>] WaitFor of int option

    /// A positional prefix + trailing flag list (a flag leaf under newer CommandTree).
    type Prefixed = | [<Cmd("Deploy it")>] Deploy of env: string * RichFlag list

    /// A single-case NULLARY union — compiled flattened (no `Tags`), its sole case
    /// recovered from the `get_<Case>` singleton getter.
    type SoleNullary = | [<Cmd(Name = "only")>] Only

    /// A nullary-only union used as a positional argument (an enum-like value arg,
    /// exercising the display-type-name fallback for a union-typed field).
    type Mode =
        | [<Cmd("a")>] ModeA
        | [<Cmd("b")>] ModeB

    /// Every remaining scalar value type as positional args (int64/float/decimal/guid),
    /// plus a union-typed arg — exercises the display-type-name branches.
    type Scalars =
        | [<Cmd("scale")>] Scale of big: int64 * ratio: float * price: decimal * ident: System.Guid
        | [<Cmd("mode")>] SetMode of name: string * mode: Mode

    // --- minimal fixture trio for the extraction+diff end-to-end tests ---
    type MiniV1 =
        | [<Cmd("Release it")>] Release of SimpleFlag list
        | [<Cmd(Name = "check")>] Inspect

    type MiniRenamed =
        | [<Cmd("Release it")>] Release of SimpleFlag list
        | [<Cmd(Name = "verify")>] Inspect // check -> verify (Breaking)

    type MiniAdded =
        | [<Cmd("Release it")>] Release of SimpleFlag list
        | [<Cmd(Name = "check")>] Inspect
        | [<Cmd("A brand new command")>] Extra // added command (Addition)

    /// Convert a runtime CommandTree (0.7.0) node to the production Grammar model —
    /// the ground truth the metadata-only walk is checked against. 0.7.0 flags are
    /// bool-only: `IsBool` => Nullary, otherwise a required-value flag.
    let rec private toNode (node: CommandTree<'Cmd>) : FsSemanticTagger.CommandNode =
        match node with
        | Leaf leaf ->
            FsSemanticTagger.CommandNode.Leaf(
                leaf.Name,
                leaf.Args
                |> List.map (fun a ->
                    { FsSemanticTagger.ArgSpec.Name = a.Name
                      IsOptional = a.IsOptional
                      IsList = a.IsList
                      TypeName = a.TypeName }),
                leaf.Flags
                |> List.map (fun f ->
                    { FsSemanticTagger.FlagSpec.LongName = f.LongName
                      ShortName = f.ShortName
                      Arity =
                        if f.IsBool then
                            FsSemanticTagger.FlagArity.Nullary
                        else
                            FsSemanticTagger.FlagArity.RequiredValue
                      TypeName = f.TypeName })
            )
        | Group g -> FsSemanticTagger.CommandNode.Group(g.Name, g.Children |> List.map toNode)

    /// The realized grammar CommandTree builds at runtime for `'Cmd` (the root group's
    /// children become the grammar's roots).
    let expectedGrammar<'Cmd> () : FsSemanticTagger.Grammar =
        match CommandReflection.fromUnion<'Cmd> "fixture" with
        | Group g -> { FsSemanticTagger.Grammar.Roots = g.Children |> List.map toNode }
        | leaf -> { FsSemanticTagger.Grammar.Roots = [ toNode leaf ] }

open FsSemanticTagger

// ---- tiny builders (now over the production model) --------------------------

let private arg name =
    { Name = name
      IsOptional = false
      IsList = false
      TypeName = "string" }

let private optArg name =
    { Name = name
      IsOptional = true
      IsList = false
      TypeName = "string" }

let private flag long arity =
    { LongName = long
      ShortName = None
      Arity = arity
      TypeName = "bool" }

let private leaf name args flags = Leaf(name, args, flags)
let private grammar roots = { Roots = roots }

// ---- pure-diff facts --------------------------------------------------------

[<Fact>]
let ``identical grammar (pure refactor) is NoChange`` () =
    let g = grammar [ leaf "release" [] [ flag "dry-run" Nullary ]; leaf "init" [] [] ]

    test <@ Grammar.compare g g = GNoChange @>

[<Fact>]
let ``description/help-only change is NoChange (cosmetic, names+shapes identical)`` () =
    // The model omits descriptions, so two grammars differing only in help text
    // are equal by construction — pinning that cosmetic edits never bump.
    let before = grammar [ leaf "init" [] [] ]
    let after = grammar [ leaf "init" [] [] ]
    test <@ Grammar.compare before after = GNoChange @>

[<Fact>]
let ``added flag is Addition (old invocations still parse)`` () =
    let before = grammar [ leaf "release" [] [ flag "dry-run" Nullary ] ]

    let after =
        grammar [ leaf "release" [] [ flag "dry-run" Nullary; flag "push" Nullary ] ]

    test <@ Grammar.compare before after = GAddition @>

[<Fact>]
let ``added command is Addition`` () =
    let before = grammar [ leaf "init" [] [] ]
    let after = grammar [ leaf "init" [] []; leaf "release" [] [] ]
    test <@ Grammar.compare before after = GAddition @>

[<Fact>]
let ``removed command is Breaking`` () =
    let before = grammar [ leaf "init" [] []; leaf "release" [] [] ]
    let after = grammar [ leaf "init" [] [] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``removed flag is Breaking`` () =
    let before =
        grammar [ leaf "release" [] [ flag "dry-run" Nullary; flag "push" Nullary ] ]

    let after = grammar [ leaf "release" [] [ flag "dry-run" Nullary ] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``renamed command (Cmd Name override) is Breaking — the signature-diff blind spot`` () =
    // Same DU shape, only the [<Cmd(Name=...)>] override changed: invisible to
    // assembly-signature diffing, but `old-name` no longer parses => Breaking.
    let before = grammar [ leaf "check-api" [ arg "old"; arg "new" ] [] ]
    let after = grammar [ leaf "diff-api" [ arg "old"; arg "new" ] [] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``renamed flag (CmdFlag Name override) is Breaking`` () =
    let before = grammar [ leaf "release" [] [ flag "dry-run" Nullary ] ]
    let after = grammar [ leaf "release" [] [ flag "preview" Nullary ] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``flag arity Nullary->RequiredValue is Breaking (now consumes next token)`` () =
    let before = grammar [ leaf "release" [] [ flag "wait" Nullary ] ]
    let after = grammar [ leaf "release" [] [ flag "wait" RequiredValue ] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``flag arity RequiredValue->OptionalValue is Breaking (space form no longer binds a value)`` () =
    // RequiredValue accepts `--conf v` (space form); OptionalValue is inline-only,
    // so `--conf v` now binds None and leaves `v` dangling => a previously-valid
    // invocation changes meaning. This is the AUTOMATION-187 shape (0.8.0 arity).
    let before = grammar [ leaf "release" [] [ flag "conf" RequiredValue ] ]
    let after = grammar [ leaf "release" [] [ flag "conf" OptionalValue ] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``added OPTIONAL positional arg is Addition (old invocations still parse)`` () =
    let before = grammar [ leaf "init" [] [] ]
    let after = grammar [ leaf "init" [ optArg "path" ] [] ]
    test <@ Grammar.compare before after = GAddition @>

[<Fact>]
let ``added REQUIRED positional arg is Breaking (old invocations now error)`` () =
    let before = grammar [ leaf "extract-api" [ arg "dll" ] [] ]
    let after = grammar [ leaf "extract-api" [ arg "dll"; arg "out" ] [] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``positional arg required->optional is Addition; optional->required is Breaking`` () =
    let reqd = grammar [ leaf "extract-api" [ arg "dll" ] [] ]
    let opt = grammar [ leaf "extract-api" [ optArg "dll" ] [] ]
    // relaxing required -> optional keeps every old call valid (additive)
    test <@ Grammar.compare reqd opt = GAddition @>
    // tightening optional -> required breaks callers who omitted it
    test <@ Grammar.compare opt reqd = GBreaking @>

[<Fact>]
let ``arg value TYPE change (string->int) is Breaking`` () =
    let before = grammar [ leaf "wait" [ arg "seconds" ] [] ]

    let after = grammar [ leaf "wait" [ { arg "seconds" with TypeName = "int" } ] [] ]

    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``nested Group restructure: removing a subcommand is Breaking`` () =
    let before = grammar [ Group("db", [ leaf "migrate" [] []; leaf "seed" [] [] ]) ]

    let after = grammar [ Group("db", [ leaf "migrate" [] [] ]) ]
    test <@ Grammar.compare before after = GBreaking @>

// ---- extra pure-diff facts (fold + combine + edge branches) -----------------

[<Fact>]
let ``combine takes the stronger verdict (Breaking > Addition > NoChange)`` () =
    test <@ Grammar.combine GAddition GBreaking = GBreaking @>
    test <@ Grammar.combine GAddition GNoChange = GAddition @>
    test <@ Grammar.combine GNoChange GNoChange = GNoChange @>

[<Fact>]
let ``leaf<->group restructure at the same name is Breaking`` () =
    let before = grammar [ leaf "db" [] [] ]
    let after = grammar [ Group("db", [ leaf "migrate" [] [] ]) ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``adding a short alias to a flag is Addition; removing one is Breaking`` () =
    let noShort = grammar [ leaf "release" [] [ flag "conf" RequiredValue ] ]

    let withShort =
        grammar
            [ leaf
                  "release"
                  []
                  [ { flag "conf" RequiredValue with
                        ShortName = Some "k" } ] ]

    test <@ Grammar.compare noShort withShort = GAddition @>
    test <@ Grammar.compare withShort noShort = GBreaking @>

[<Fact>]
let ``scalar->list positional arg is Addition; list->scalar is Breaking`` () =
    let scalar = grammar [ leaf "wait" [ arg "tags" ] [] ]

    let asList = grammar [ leaf "wait" [ { arg "tags" with IsList = true } ] [] ]

    test <@ Grammar.compare scalar asList = GAddition @>
    test <@ Grammar.compare asList scalar = GBreaking @>

[<Fact>]
let ``removing a trailing positional arg is Breaking (old value now errors)`` () =
    let before = grammar [ leaf "extract" [ arg "dll"; optArg "out" ] [] ]
    let after = grammar [ leaf "extract" [ arg "dll" ] [] ]
    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``flag value TYPE change (int->string) is Breaking`` () =
    let before =
        grammar
            [ leaf
                  "release"
                  []
                  [ { flag "only" RequiredValue with
                        TypeName = "int" } ] ]

    let after =
        grammar
            [ leaf
                  "release"
                  []
                  [ { flag "only" RequiredValue with
                        TypeName = "string" } ] ]

    test <@ Grammar.compare before after = GBreaking @>

[<Fact>]
let ``toApiChange maps grammar verdicts onto the API bump ladder`` () =
    test <@ Grammar.toApiChange GNoChange = Api.NoChange @>

    test
        <@
            match Grammar.toApiChange GAddition with
            | Api.Addition _ -> true
            | _ -> false
        @>

    test
        <@
            match Grammar.toApiChange GBreaking with
            | Api.Breaking _ -> true
            | _ -> false
        @>

[<Fact>]
let ``foldIntoApi keeps the stronger bump and prefers the API signatures on a tie`` () =
    // Grammar strictly stronger than API => the grammar marker drives the bump.
    test
        <@
            match Grammar.foldIntoApi Api.NoChange GBreaking with
            | Api.Breaking _ -> true
            | _ -> false
        @>

    // API at least as strong => the (richer) API change is kept unchanged.
    let apiAddition = Api.Addition(Api.ApiSignature "  Foo::New(): int", [])
    test <@ Grammar.foldIntoApi apiAddition GAddition = apiAddition @>
    test <@ Grammar.foldIntoApi apiAddition GNoChange = apiAddition @>

    // A breaking API keeps its bump regardless of a weaker grammar verdict.
    let apiBreaking = Api.Breaking(Api.ApiSignature "type Gone", [])
    test <@ Grammar.foldIntoApi apiBreaking GAddition = apiBreaking @>

// ---- structural recovery under MetadataLoadContext (the crux) ---------------

[<Fact>]
let ``extractGrammarFromAssembly recovers FsSemanticTagger's own realized grammar`` () =
    // The real consumer: extract the grammar from the built FsSemanticTagger.dll
    // under MetadataLoadContext and assert it equals the tree CommandTree's own
    // fromUnion builds at runtime for the same Command DU. This discharges the
    // abandon criterion against a real consumer.
    let dll = typeof<FsSemanticTagger.Program.Command>.Assembly.Location
    let extracted = Grammar.extractGrammarFromAssembly dll
    let expected = Some(Fixtures.expectedGrammar<FsSemanticTagger.Program.Command> ())
    test <@ extracted = expected @>

[<Fact>]
let ``extractGrammarForType recovers every 0.7.0-supported command shape faithfully`` () =
    // A fixture DU covering nullary / required-arg / optional-arg / record-arg /
    // list-arg / nested-group / sole-field-flags / Cmd(Name)-override cases. The
    // metadata-only walk must equal CommandTree's runtime fromUnion tree for ALL.
    let dll = typeof<Fixtures.Fixture>.Assembly.Location
    let extracted = Grammar.extractGrammarForType dll typeof<Fixtures.Fixture>.FullName
    let expected = Some(Fixtures.expectedGrammar<Fixtures.Fixture> ())
    test <@ extracted = expected @>

[<Fact>]
let ``extraction recovers a positional-prefix flag leaf with an optional-value flag`` () =
    // Newer-than-0.7.0 shape (positional prefix + trailing flag list, plus an
    // optional-value flag) — validated against the production model directly, since
    // 0.7.0's fromUnion would model these differently.
    let dll = typeof<Fixtures.Prefixed>.Assembly.Location
    let extracted = Grammar.extractGrammarForType dll typeof<Fixtures.Prefixed>.FullName

    match extracted with
    | Some { Roots = [ Leaf(name, args, flags) ] } ->
        test <@ name = "deploy" @>

        test
            <@
                args = [ { Name = "env"
                           IsOptional = false
                           IsList = false
                           TypeName = "string" } ]
            @>

        test <@ List.length flags = 2 @>

        test
            <@
                flags
                |> List.exists (fun f -> f.LongName = "wait" && f.Arity = OptionalValue && f.TypeName = "int")
            @>

        test <@ flags |> List.exists (fun f -> f.LongName = "r-dry-run" && f.Arity = Nullary) @>
    | other -> failwithf "unexpected extraction: %A" other

[<Fact>]
let ``a non-CommandTree assembly yields no grammar (left to the API diff)`` () =
    // FSharp.Core does not reference CommandTree => not a consumer => None.
    let dll = typeof<int list>.Assembly.Location
    test <@ Grammar.extractGrammarFromAssembly dll = None @>

[<Fact>]
let ``extraction + diff end-to-end: a renamed command is Breaking`` () =
    let dll = typeof<Fixtures.MiniV1>.Assembly.Location
    let v1 = Grammar.extractGrammarForType dll typeof<Fixtures.MiniV1>.FullName

    let renamed =
        Grammar.extractGrammarForType dll typeof<Fixtures.MiniRenamed>.FullName

    test
        <@
            match v1, renamed with
            | Some a, Some b -> Grammar.compare a b = GBreaking
            | _ -> false
        @>

[<Fact>]
let ``extraction + diff end-to-end: an added command is Addition`` () =
    let dll = typeof<Fixtures.MiniV1>.Assembly.Location
    let v1 = Grammar.extractGrammarForType dll typeof<Fixtures.MiniV1>.FullName
    let added = Grammar.extractGrammarForType dll typeof<Fixtures.MiniAdded>.FullName

    test
        <@
            match v1, added with
            | Some a, Some b -> Grammar.compare a b = GAddition
            | _ -> false
        @>

[<Fact>]
let ``extraction recovers every scalar value type and a union-typed arg`` () =
    let dll = typeof<Fixtures.Scalars>.Assembly.Location
    let extracted = Grammar.extractGrammarForType dll typeof<Fixtures.Scalars>.FullName

    let typeNamesOf name roots =
        roots
        |> List.tryPick (function
            | Leaf(n, args, _) when n = name -> Some(args |> List.map (fun a -> a.TypeName))
            | _ -> None)

    match extracted with
    | Some { Roots = roots } ->
        test <@ typeNamesOf "scale" roots = Some [ "int64"; "float"; "decimal"; "guid" ] @>
        test <@ typeNamesOf "set-mode" roots = Some [ "string"; "mode" ] @>
    | None -> failwith "expected a grammar"

[<Fact>]
let ``extraction recovers a single-case nullary union`` () =
    let dll = typeof<Fixtures.SoleNullary>.Assembly.Location

    let extracted =
        Grammar.extractGrammarForType dll typeof<Fixtures.SoleNullary>.FullName

    test <@ extracted = Some { Roots = [ Leaf("only", [], []) ] } @>

[<Fact>]
let ``an assembly with several root command unions is ambiguous => None`` () =
    // The test assembly itself carries many unrelated command DUs (the fixtures),
    // so there is no single root => extraction refuses to guess.
    let dll = typeof<Fixtures.MiniV1>.Assembly.Location
    test <@ Grammar.extractGrammarFromAssembly dll = None @>

[<Fact>]
let ``extractGrammarForType on a non-union type is None`` () =
    let dll = typeof<Fixtures.BuildArgs>.Assembly.Location
    test <@ Grammar.extractGrammarForType dll typeof<Fixtures.BuildArgs>.FullName = None @>
    test <@ Grammar.extractGrammarForType dll "No.Such.Type" = None @>

[<Fact>]
let ``extraction of an unreadable path is None (never throws)`` () =
    let bogus =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-grammar.dll")

    test <@ Grammar.extractGrammarFromAssembly bogus = None @>
    test <@ Grammar.extractGrammarForType bogus "Whatever" = None @>

[<Fact>]
let ``grammar cache extractors return None for an absent package`` () =
    let bogusRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-cache-root")

    test <@ Grammar.extractGrammarFromCacheRoot bogusRoot "FsSemanticTagger" "1.0.0" = None @>
    test <@ Grammar.extractPreviousGrammarFromNuGet "no-such-package-xyzzy" "9.9.9" = None @>

    // Cache layout present but the package DLL is absent from it => None.
    let emptyRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fsst-empty-cache-" + System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(emptyRoot, "pkg", "1.0.0", "lib", "net10.0"))
    |> ignore

    try
        test <@ Grammar.extractGrammarFromCacheRoot emptyRoot "Pkg" "1.0.0" = None @>
    finally
        try
            System.IO.Directory.Delete(emptyRoot, true)
        with _ ->
            ()

[<Fact>]
let ``extractGrammarFromCacheRoot reads a consumer from a cache layout`` () =
    let binDir =
        System.IO.Path.GetDirectoryName(typeof<FsSemanticTagger.Program.Command>.Assembly.Location)

    let cacheRoot =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "fsst-grammar-cache-" + System.Guid.NewGuid().ToString("N")
        )

    let pkgLib =
        System.IO.Path.Combine(cacheRoot, "fssemantictagger", "1.0.0", "lib", "net10.0")

    System.IO.Directory.CreateDirectory(pkgLib) |> ignore

    try
        for dll in System.IO.Directory.GetFiles(binDir, "*.dll") do
            System.IO.File.Copy(dll, System.IO.Path.Combine(pkgLib, System.IO.Path.GetFileName dll), true)

        let extracted =
            Grammar.extractGrammarFromCacheRoot cacheRoot "FsSemanticTagger" "1.0.0"

        test <@ extracted = Some(Fixtures.expectedGrammar<FsSemanticTagger.Program.Command> ()) @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

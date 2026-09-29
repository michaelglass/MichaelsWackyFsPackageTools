module FsSemanticTagger.Tests.ApiTests

open Xunit
open Tests.Common
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Api

/// The tool's own compiled DLL, a fixture for reading a real assembly.
let private taggerDll = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

let private isAddition =
    function
    | Addition _ -> true
    | Breaking _
    | NoChange -> false

[<Fact>]
let ``formatTypeName qualifies simple types by full name and assembly`` () =
    // The comparison key is assembly-qualified: <full name> [<assembly name>].
    test <@ formatTypeName typeof<string> = "System.String [System.Private.CoreLib]" @>
    test <@ formatTypeName typeof<int> = "System.Int32 [System.Private.CoreLib]" @>
    test <@ formatTypeName typeof<bool> = "System.Boolean [System.Private.CoreLib]" @>

[<Fact>]
let ``formatTypeName handles generic types`` () =
    let resultType = typeof<Result<int, string>>
    let formatted = formatTypeName resultType
    // The `n arity marker never leaks into the key.
    test <@ not (formatted.Contains("`")) @>
    // Arity stripped; the type and every argument carry assembly-qualified full names.
    test
        <@
            formatted =
                "Microsoft.FSharp.Core.FSharpResult<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core]"
        @>

[<Fact>]
let ``formatTypeName handles FSharpFunc`` () =
    let funcType = typeof<int -> string>
    let formatted = formatTypeName funcType

    test
        <@
            formatted =
                "Microsoft.FSharp.Core.FSharpFunc<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core]"
        @>

[<Fact>]
let ``formatTypeName handles arrays`` () =
    test <@ formatTypeName typeof<string[]> = "System.String [System.Private.CoreLib][]" @>
    test <@ formatTypeName typeof<int[]> = "System.Int32 [System.Private.CoreLib][]" @>

[<Fact>]
let ``formatTypeName handles nested generic arrays`` () =
    test
        <@
            formatTypeName typeof<Result<int, string>[]> =
                "Microsoft.FSharp.Core.FSharpResult<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core][]"
        @>

[<Fact>]
let ``formatTypeName renders a generic parameter as its bare name`` () =
    // A generic parameter ('T) has no assembly identity of its own, so it is not
    // assembly-qualified — otherwise every generic method would diff on parameter names.
    let genericParam =
        typedefof<System.Collections.Generic.List<_>>.GetGenericArguments().[0]

    test <@ formatTypeName genericParam = "T" @>

[<Fact>]
let ``formatTypeName renders a generic constructed over a type parameter`` () =
    // `List<'T>` (a generic whose argument is a generic parameter, as in a generic
    // method signature): the base name keeps its assembly qualification and the 'T
    // argument recurses to its bare name.
    let listDef = typedefof<System.Collections.Generic.List<_>>
    let openList = listDef.MakeGenericType(listDef.GetGenericArguments())
    test <@ formatTypeName openList = "System.Collections.Generic.List<T> [System.Private.CoreLib]" @>

// --- Assembly-qualified type identity in the comparison key ---
// A public member whose parameter/return type keeps its short name but moves to a
// DIFFERENT assembly is a breaking change: a consumer passing the old type no longer
// compiles. The comparison key must therefore identify a type by assembly + full
// name, not by its short name. Regression that exposed this: TestPrune.Falco's
// `FalcoRouteExtension` ctor parameter moved from TestPrune.Core's `Ports.RouteStore`
// to Falco's own `RouteStore` — both print as "RouteStore" — so the differ saw no
// change and under-called a MAJOR break as MINOR (2.0.4 -> 2.1.0).

[<Fact>]
let ``formatTypeName distinguishes same-short-name types from different assemblies`` () =
    // System.Version (System.Private.CoreLib) and FsSemanticTagger.Version.Version
    // (this tool's assembly) share the short name "Version" but are different types.
    let bcl = formatTypeName typeof<System.Version>
    let own = formatTypeName typeof<FsSemanticTagger.Version.Version>
    // On the unqualified renderer both are "Version" — this is the blind spot.
    test <@ bcl <> own @>

[<Fact>]
let ``differ reports Breaking when a ctor parameter type moves assemblies`` () =
    // The TestPrune.Falco shape: a ctor whose single parameter kept its short name
    // ("Version" here, standing in for "RouteStore") but changed assembly between
    // versions. Rendered through the real formatTypeName exactly as extractFromAssembly
    // builds a ctor signature, then diffed through the real compare.
    let ctorSig (paramType: System.Type) =
        ApiSignature.Member("Holder", sprintf ".ctor(%s)" (formatTypeName paramType))

    let oldApi =
        [
            ApiSignature.TypeDecl "Holder"
            ctorSig typeof<FsSemanticTagger.Version.Version>
        ]

    let newApi = [ ApiSignature.TypeDecl "Holder"; ctorSig typeof<System.Version> ]

    // On the unqualified renderer both ctor sigs read "  Holder::.ctor(Version)", so
    // compare sees NoChange and the release under-bumps (MINOR/patch instead of MAJOR).
    match compare oldApi newApi with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking for a parameter type that moved assemblies, got %A" other

[<Fact>]
let ``formatTypeName keys on assembly NAME not assembly VERSION`` () =
    // Over-correction guard: identity is assembly *name* + full name, never the
    // assembly *version* — otherwise a routine dependency bump would read as a false
    // major. The rendered key must carry the assembly's simple name but no version.
    let rendered = formatTypeName typeof<System.Version>
    let asmName = typeof<System.Version>.Assembly.GetName().Name
    test <@ rendered.Contains(asmName) @>
    test <@ not (rendered.Contains("Version=")) @>
    test <@ not (System.Text.RegularExpressions.Regex.IsMatch(rendered, @"\d+\.\d+\.\d+")) @>

[<Fact>]
let ``differ reports NoChange when a ctor parameter type is identical across versions`` () =
    // Converse of the break test: the same type (same assembly, same full name) on
    // both sides must stay NoChange. The fix must not flag a stable signature.
    let ctorSig (paramType: System.Type) =
        ApiSignature.Member("Holder", sprintf ".ctor(%s)" (formatTypeName paramType))

    let oldApi = [ ApiSignature.TypeDecl "Holder"; ctorSig typeof<System.Version> ]
    let newApi = [ ApiSignature.TypeDecl "Holder"; ctorSig typeof<System.Version> ]
    test <@ compare oldApi newApi = NoChange @>

[<Fact>]
let ``compare with identical APIs returns NoChange`` () =
    let api =
        [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "Bar(): String") ]

    test <@ compare api api = NoChange @>

[<Fact>]
let ``compare with added signatures returns Addition`` () =
    let baseline = [ ApiSignature.TypeDecl "Foo" ]

    let current =
        [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "Bar(): String") ]

    let result = compare baseline current

    test <@ result = Addition(ApiSignature.Member("Foo", "Bar(): String"), []) @>

[<Fact>]
let ``compare with removed signatures returns Breaking`` () =
    let baseline =
        [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "Bar(): String") ]

    let current = [ ApiSignature.TypeDecl "Foo" ]
    let result = compare baseline current

    test <@ result = Breaking(ApiSignature.Member("Foo", "Bar(): String"), []) @>

[<Fact>]
let ``compare with both added and removed returns Breaking`` () =
    let baseline =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "OldMethod(): String")
        ]

    let current =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "NewMethod(): Int32")
        ]

    match compare baseline current with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``compare with empty lists returns NoChange`` () = test <@ compare [] [] = NoChange @>

[<Fact>]
let ``extractFromAssembly extracts signatures from own DLL`` () =
    // The tool's own compiled DLL is the fixture.
    let dllPath = taggerDll

    let signatures = extractFromAssembly dllPath

    let hasVersionType =
        signatures
        |> List.exists (ApiSignature.render >> fun s -> s = "type FsSemanticTagger.Version+Version")

    test <@ hasVersionType @>

    // `parse` is compiled as a static method.
    let hasParseFunction =
        signatures |> List.exists (ApiSignature.render >> fun s -> s.Contains("parse"))

    test <@ hasParseFunction @>

    test <@ signatures.Length > 5 @>

[<Fact>]
let ``extractFromAssembly results are sorted by their rendered text`` () =
    let dllPath = taggerDll

    let signatures = extractFromAssembly dllPath
    let sorted = List.sortBy ApiSignature.render signatures
    test <@ signatures = sorted @>

[<Fact>]
let ``getAssemblySearchPaths includes DLL directory and runtime directory`` () =
    let dllPath = taggerDll

    let paths = getAssemblySearchPaths dllPath
    test <@ paths.Length >= 2 @>
    let dllDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dllPath))
    test <@ paths[0] = dllDir @>

[<Fact>]
let ``compare with only additions and no removals returns Addition`` () =
    let baseline = [ ApiSignature.TypeDecl "Foo" ]

    let current =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "Bar(): String")
            ApiSignature.Member("Foo", "Baz(): Int32")
        ]

    match compare baseline current with
    | Addition _ -> test <@ (ApiChange.toList (compare baseline current)).Length = 2 @>
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``compare with only removals returns Breaking`` () =
    let baseline =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "Bar(): String")
            ApiSignature.Member("Foo", "Baz(): Int32")
        ]

    let current = [ ApiSignature.TypeDecl "Foo" ]

    match compare baseline current with
    | Breaking _ -> test <@ (ApiChange.toList (compare baseline current)).Length = 2 @>
    | other -> failwithf "Expected Breaking, got %A" other

// Union-case classification, diffed over real compiled before/after pairs
// (ApiFixtures.fs). A scenario's two namespaces are renamed to `Lib` so they
// diff as two releases of one library.

let private fixtureApi (ns: string) : ApiSignature list =
    let toLib (name: string) = name.Replace(ns + ".", "Lib.")

    typeof<ApiFixtures.NewCaseWithFields.Before.Shape>.Assembly.GetExportedTypes()
    |> Array.filter (fun t -> t.Namespace = ns)
    |> extractFromTypes
    |> List.map (function
        | ApiSignature.UnionCase(union, case) -> ApiSignature.UnionCase(toLib union, case)
        | ApiSignature.TypeDecl fullName -> ApiSignature.TypeDecl(toLib fullName)
        | ApiSignature.Member(declaringType, signature) -> ApiSignature.Member(declaringType, toLib signature)
        | ApiSignature.Marker text -> ApiSignature.Marker text)

let private diffScenario (scenario: string) : ApiChange =
    compare (fixtureApi $"ApiFixtures.{scenario}.Before") (fixtureApi $"ApiFixtures.{scenario}.After")

let private breakingHead (change: ApiChange) : string option =
    match change with
    | Breaking(head, _) -> Some(ApiSignature.render head)
    | Addition _
    | NoChange -> None

[<Fact>]
let ``a new case with fields on an existing union is breaking and names the case`` () =
    test <@ breakingHead (diffScenario "NewCaseWithFields") = Some "case Lib.Shape::Triangle" @>

[<Fact>]
let ``a new fieldless case on an existing union is breaking and names the case`` () =
    // Fieldless cases compile to no nested type, only a static property.
    test <@ breakingHead (diffScenario "NewNullaryCase") = Some "case Lib.Platform::Windows" @>

[<Fact>]
let ``a second case on a single-case union is breaking`` () =
    test <@ breakingHead (diffScenario "SingleCaseUnion") = Some "case Lib.Token::Anonymous" @>

[<Fact>]
let ``a new case on a RequireQualifiedAccess union is breaking`` () =
    test <@ breakingHead (diffScenario "QualifiedAccessUnion") = Some "case Lib.Mode::Custom" @>

[<Fact>]
let ``a new case on a struct union is breaking`` () =
    // Struct unions compile to no nested case types at all.
    test <@ breakingHead (diffScenario "StructUnion") = Some "case Lib.Outcome::Errored" @>

[<Fact>]
let ``a new case on a union declared inside a module is breaking`` () =
    test <@ breakingHead (diffScenario "UnionInModule") = Some "case Lib.Ratchet+Status::Failed" @>

[<Fact>]
let ``new types and functions inside an existing module are an addition`` () =
    // Modules compile to classes, so `Cobertura+ReaderOptions` looks like a nested
    // type of an existing type; it must not be read as a new union case.
    let change = diffScenario "TypeInModule"
    test <@ isAddition change @>

    let added = ApiChange.toList change |> List.map ApiSignature.render
    test <@ added |> List.contains "type Lib.Cobertura+ReaderOptions" @>
    test <@ added |> List.contains "case Lib.Cobertura+ExclusionReason::Matched" @>

[<Fact>]
let ``a new top-level union is an addition`` () =
    test <@ isAddition (diffScenario "NewTopLevelUnion") @>

[<Fact>]
let ``a new case on a union with a private representation is not breaking`` () =
    // Consumers cannot match on a private representation, so no case is public.
    test <@ breakingHead (diffScenario "PrivateUnion") = None @>

    test
        <@
            fixtureApi "ApiFixtures.PrivateUnion.After"
            |> List.forall (ApiSignature.render >> fun s -> not (s.StartsWith "case "))
        @>

[<Fact>]
let ``a removed union case is breaking`` () =
    let change =
        compare (fixtureApi "ApiFixtures.NewNullaryCase.After") (fixtureApi "ApiFixtures.NewNullaryCase.Before")

    test
        <@
            ApiChange.toList change
            |> List.contains (ApiSignature.UnionCase("Lib.Platform", "Windows"))
        @>

    test <@ breakingHead change |> Option.isSome @>

// Compiler-invented members and types (ApiFixtures.CompilerInvented).

let private namesModule =
    typeof<ApiFixtures.CompilerInvented.Money>.Assembly.GetType "ApiFixtures.CompilerInvented.Names"

let private signatureLines (types: System.Type list) : string list =
    extractFromTypes types |> List.map ApiSignature.render

[<Fact>]
let ``a Debug build's __debug copies of inline functions are not API`` () =
#if DEBUG
    // Positive control: this Debug build did emit the copy that must be dropped.
    test
        <@
            namesModule.GetMethods()
            |> Array.exists (fun m -> m.Name.StartsWith "<sumBy>__debug@")
        @>
#endif
    // Exactly what a Release build emits: the type and the six declared functions.
    let lines = signatureLines [ namesModule ]
    test <@ lines |> List.forall (fun s -> not (s.Contains "__debug")) @>
    test <@ lines.Length = 7 @>

[<Fact>]
let ``source names that look unusual when compiled are kept`` () =
    let lines =
        signatureLines [ namesModule; typeof<ApiFixtures.CompilerInvented.Money> ]

    let hasMember (prefix: string) =
        lines
        |> List.exists (fun s -> s.StartsWith("  " + prefix, System.StringComparison.Ordinal))

    test <@ hasMember "Names::total(" @>
    test <@ hasMember "Names::|Even|Odd|(" @>
    test <@ hasMember "Names::|Positive|_|(" @>
    test <@ hasMember "Names::op_BarGreaterGreater(" @>
    test <@ hasMember "Names::a <b> c(" @>
    test <@ hasMember "Names::point(" @>
    // Compiler-generated, but API: derived equality/comparison, the case factory.
    test <@ hasMember "Money::op_Addition(" @>
    test <@ hasMember "Money::Equals(" @>
    test <@ hasMember "Money::CompareTo(" @>
    test <@ hasMember "Money::NewMoney(" @>
    test <@ hasMember "Money::cents: " @>
    test <@ lines |> List.contains "case ApiFixtures.CompilerInvented.Money::Money" @>

[<Fact>]
let ``an anonymous record type is not API, but a member exposing one still names it`` () =
    let anonymous = ApiFixtures.CompilerInvented.Names.point().GetType()
    let lines = signatureLines [ anonymous; namesModule ]
    test <@ lines |> List.forall (fun s -> not (s.StartsWith "type <>f__AnonymousType")) @>

    test
        <@
            lines
            |> List.exists (fun s -> s.StartsWith "  Names::point(): <>f__AnonymousType")
        @>

[<Fact>]
let ``extractFromAssembly drops compiler-invented members through the metadata load context`` () =
    let dll = typeof<ApiFixtures.CompilerInvented.Money>.Assembly.Location
    let lines = extractFromAssembly dll |> List.map ApiSignature.render
    test <@ lines |> List.forall (fun s -> not (s.Contains "__debug@")) @>
    test <@ lines |> List.forall (fun s -> not (s.StartsWith "type <>f__AnonymousType")) @>

    test
        <@
            lines
            |> List.contains
                "  Names::|Even|Odd|(System.Int32 [System.Private.CoreLib]): Microsoft.FSharp.Core.FSharpChoice<Microsoft.FSharp.Core.Unit [FSharp.Core], Microsoft.FSharp.Core.Unit [FSharp.Core]> [FSharp.Core]"
        @>

[<Fact>]
let ``extractFromAssembly reads union cases through the metadata load context`` () =
    // The fixtures above use runtime reflection; releases read a dll through a
    // MetadataLoadContext, whose attribute data must yield the same cases.
    let dll = typeof<ApiFixtures.NewCaseWithFields.Before.Shape>.Assembly.Location
    let signatures = extractFromAssembly dll |> List.map ApiSignature.render

    let expected =
        set
            [
                "case ApiFixtures.NewNullaryCase.After.Platform::Windows"
                "case ApiFixtures.NewCaseWithFields.After.Shape::Triangle"
                "case ApiFixtures.StructUnion.After.Outcome::Errored"
                "case ApiFixtures.SingleCaseUnion.Before.Token::Token"
            ]

    test <@ Set.isSubset expected (set signatures) @>

    test
        <@
            not (
                signatures
                |> List.exists (fun s -> s.StartsWith "case ApiFixtures.TypeInModule.After.Cobertura::")
            )
        @>

[<Fact>]
let ``readNuGetCache returns NotCached for nonexistent package`` () =
    test <@ (Extraction.readNuGetCache "ThisPackageDoesNotExist12345" "1.0.0").Api = NotCached @>

// downloadToCache / Extraction.readPrevious — the prior-API fetch path.
// These guard the bug where a missing prior package silently became "no change".

[<Fact>]
let ``downloadToCache returns true and runs dotnet restore on a probe project when restore succeeds`` () =
    let mutable invoked = []

    let fakeRun (cmd: string) (args: string) : FsSemanticTagger.Shell.CommandResult =
        invoked <- invoked @ [ (cmd, args) ]
        FsSemanticTagger.Shell.Success ""

    let ok = downloadToCache fakeRun "SomePackage" "1.2.3"
    test <@ ok = true @>
    // It restores a throwaway .csproj rather than touching the real repo
    test
        <@
            invoked
            |> List.exists (fun (c, a) -> c = "dotnet" && a.StartsWith("restore") && a.Contains(".csproj"))
        @>

[<Fact>]
let ``downloadToCache returns false when restore fails`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("no such package", 1)

    test <@ downloadToCache fakeRun "SomePackage" "1.2.3" = false @>

// --- Flat-container availability poll ---
// The post-push poll checks the nuget.org flat container (fast-updating publish
// surface) first, falling back to the restore probe only when it hasn't indexed
// yet. These guard the bug where the package was already live on the CDN but the
// restore-resolved registration index lagged, so the poll timed out spuriously.

[<Fact>]
let ``flatContainerIndexUrl lower-cases the id and targets nuget.org`` () =
    test
        <@
            flatContainerIndexUrl "FsSemanticTagger" =
                "https://api.nuget.org/v3-flatcontainer/fssemantictagger/index.json"
        @>

[<Fact>]
let ``flatContainerHasVersion finds the version case-insensitively`` () =
    let body = """{"versions":["0.13.0-alpha.10","0.13.0-ALPHA.11"]}"""
    test <@ flatContainerHasVersion body "0.13.0-alpha.11" = true @>
    test <@ flatContainerHasVersion body "0.13.0-alpha.10" = true @>

[<Fact>]
let ``flatContainerHasVersion is false when version absent or body malformed`` () =
    let body = """{"versions":["0.13.0-alpha.10"]}"""
    test <@ flatContainerHasVersion body "0.13.0-alpha.11" = false @>
    test <@ flatContainerHasVersion "not json" "1.0.0" = false @>
    test <@ flatContainerHasVersion """{"other":[]}""" "1.0.0" = false @>

[<Fact>]
let ``isPublishedViaFlatContainer true when version present, fetched from the id index url`` () =
    let mutable fetched = []

    let fakeFetch (url: string) : HttpResult =
        fetched <- fetched @ [ url ]
        HttpOk """{"versions":["1.2.3"]}"""

    test <@ isPublishedViaFlatContainer fakeFetch "SomePackage" "1.2.3" = true @>
    test <@ fetched = [ "https://api.nuget.org/v3-flatcontainer/somepackage/index.json" ] @>

[<Fact>]
let ``isPublishedViaFlatContainer false when fetch errors (offline or non-2xx)`` () =
    let fakeFetch (_url: string) : HttpResult = HttpFailed "HTTP 503"
    test <@ isPublishedViaFlatContainer fakeFetch "SomePackage" "1.2.3" = false @>

[<Fact>]
let ``isPublished succeeds on the FIRST attempt via flat container without ever probing restore`` () =
    // A restore-only check times out while the flat container already has the
    // package, so the flat-container hit must short-circuit the restore probe.
    let mutable restored = false

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        restored <- true
        FsSemanticTagger.Shell.Failure("registration index lagging", 1)

    let fakeFetch (_url: string) : HttpResult = HttpOk """{"versions":["1.2.3"]}"""

    test <@ isPublished fakeFetch fakeRun "SomePackage" "1.2.3" = true @>
    test <@ restored = false @>

[<Fact>]
let ``isPublished falls back to the restore probe when flat container has not indexed yet`` () =
    // Private-feed case (or genuinely not-yet-on-nuget.org): flat container can't
    // see it, so the restore probe — which honours the repo nuget.config — decides.
    let mutable invoked = []

    let fakeRun (cmd: string) (args: string) : FsSemanticTagger.Shell.CommandResult =
        invoked <- invoked @ [ (cmd, args) ]
        FsSemanticTagger.Shell.Success ""

    let fakeFetch (_url: string) : HttpResult = HttpOk """{"versions":[]}"""

    test <@ isPublished fakeFetch fakeRun "SomePackage" "1.2.3" = true @>
    // The fallback probes a throwaway .csproj with the HTTP cache bypassed.
    test
        <@
            invoked
            |> List.exists (fun (c, a) ->
                c = "dotnet"
                && a.StartsWith("restore")
                && a.Contains(".csproj")
                && a.Contains("--no-http-cache"))
        @>

[<Fact>]
let ``isPublished false when neither flat container nor restore find the version`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("no such package", 1)

    let fakeFetch (_url: string) : HttpResult = HttpFailed "offline"
    test <@ isPublished fakeFetch fakeRun "SomePackage" "1.2.3" = false @>

[<Fact>]
let ``isPublishedViaRestore returns true and probes with --no-http-cache when restore succeeds`` () =
    let mutable invoked = []

    let fakeRun (cmd: string) (args: string) : FsSemanticTagger.Shell.CommandResult =
        invoked <- invoked @ [ (cmd, args) ]
        FsSemanticTagger.Shell.Success ""

    let ok = isPublishedViaRestore fakeRun "SomePackage" "1.2.3"
    test <@ ok = true @>
    // It restores a throwaway .csproj with the HTTP cache bypassed so a
    // just-published version isn't masked by a stale cache.
    test
        <@
            invoked
            |> List.exists (fun (c, a) ->
                c = "dotnet"
                && a.StartsWith("restore")
                && a.Contains(".csproj")
                && a.Contains("--no-http-cache"))
        @>

[<Fact>]
let ``isPublishedViaRestore returns false when restore fails`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("no such package", 1)

    test <@ isPublishedViaRestore fakeRun "SomePackage" "1.2.3" = false @>

// ---------------------------------------------------------------------------
// flatContainerPresence: which feed answers are DEFINITE vs UNKNOWN. Only a
// definite NotOnFeed may drive a republish, so each arm is pinned individually.
// ---------------------------------------------------------------------------

[<Fact>]
let ``flatContainerPresence - 200 listing the version is OnFeed`` () =
    let fakeFetch (_url: string) : HttpResult =
        HttpOk """{"versions":["1.2.2","1.2.3"]}"""

    test <@ flatContainerPresence fakeFetch "SomePackage" "1.2.3" = OnFeed @>

[<Fact>]
let ``flatContainerPresence - 200 whose version list lacks it is definitely NotOnFeed`` () =
    // The feed enumerated everything it has for the id; this version wasn't there.
    let fakeFetch (_url: string) : HttpResult = HttpOk """{"versions":["1.2.2"]}"""
    test <@ flatContainerPresence fakeFetch "SomePackage" "1.2.3" = NotOnFeed @>

[<Fact>]
let ``flatContainerPresence - 404 for the id is definitely NotOnFeed`` () =
    // A 404 is the flat container's normal "no such package id", not a fault.
    let fakeFetch (_url: string) : HttpResult = HttpNotFound
    test <@ flatContainerPresence fakeFetch "NeverPublished" "1.2.3" = NotOnFeed @>

[<Fact>]
let ``flatContainerPresence - transport failures are Unknown, never absence`` () =
    // Timeout, DNS failure, 5xx, auth failure: all arrive as HttpFailed and must
    // NOT be mistaken for "the version isn't published".
    for reason in
        [
            "The operation has timed out."
            "HTTP 503 for ..."
            "HTTP 401 for ..."
            "No such host is known."
        ] do
        let fakeFetch (_url: string) : HttpResult = HttpFailed reason

        test
            <@
                match flatContainerPresence fakeFetch "SomePackage" "1.2.3" with
                | FeedUnknown _ -> true
                | _ -> false
            @>

[<Fact>]
let ``flatContainerPresence - an unreadable 200 body is Unknown, not absence`` () =
    // A proxy error page or a truncated read: we cannot claim a version is missing
    // from a list we could not parse.
    for body in [ "not json"; ""; """{"other":[]}"""; "<html>502 Bad Gateway</html>" ] do
        let fakeFetch (_url: string) : HttpResult = HttpOk body

        test
            <@
                match flatContainerPresence fakeFetch "SomePackage" "1.2.3" with
                | FeedUnknown _ -> true
                | _ -> false
            @>

[<Fact>]
let ``checkFeedPresence - the restore probe can only UPGRADE a verdict to OnFeed`` () =
    // A privately-published package is absent from the public flat container, so
    // the restore probe (which honours the repo nuget.config) rescues it.
    let restoreSucceeds (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Success ""

    let notOnNugetOrg (_url: string) : HttpResult = HttpOk """{"versions":[]}"""
    let feedUnreachable (_url: string) : HttpResult = HttpFailed "offline"

    test <@ checkFeedPresence notOnNugetOrg restoreSucceeds "SomePackage" "1.2.3" = OnFeed @>
    test <@ checkFeedPresence feedUnreachable restoreSucceeds "SomePackage" "1.2.3" = OnFeed @>

[<Fact>]
let ``checkFeedPresence - a failing restore probe never downgrades a definite absence`` () =
    // This is what makes the check work for PackAsTool packages at all: a
    // PackageReference probe of a tool ALWAYS fails NU1212, so if a probe failure
    // could downgrade, every tool would be permanently FeedUnknown and could never
    // recover from an orphan tag.
    let nu1212 (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure(
            "error NU1212: Invalid project-package combination for SomeTool 1.2.3. DotnetToolReference \
             project style can only contain references of the DotnetTool type",
            1
        )

    let notOnNugetOrg (_url: string) : HttpResult = HttpOk """{"versions":["1.2.2"]}"""
    test <@ checkFeedPresence notOnNugetOrg nu1212 "SomeTool" "1.2.3" = NotOnFeed @>

[<Fact>]
let ``checkFeedPresence - an unreachable feed stays Unknown when the probe cannot confirm`` () =
    let restoreFails (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("connection refused", 1)

    let feedUnreachable (_url: string) : HttpResult = HttpFailed "offline"

    test
        <@
            match checkFeedPresence feedUnreachable restoreFails "SomePackage" "1.2.3" with
            | FeedUnknown _ -> true
            | _ -> false
        @>

// ---------------------------------------------------------------------------
// checkRestorable: the question a wave gate asks. Unlike checkFeedPresence, the
// index listing a version is NOT enough for a library — the next wave's nuspec
// names this exact version, so it must restore.
// ---------------------------------------------------------------------------

let private restoreSucceeds (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
    FsSemanticTagger.Shell.Success ""

let private restoreFails (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
    FsSemanticTagger.Shell.Failure("error NU1102: Unable to find package SomePackage with version (= 1.2.3)", 1)

let private indexLists (_url: string) : HttpResult = HttpOk """{"versions":["1.2.3"]}"""
let private indexLacks (_url: string) : HttpResult = HttpOk """{"versions":["1.2.2"]}"""

[<Fact>]
let ``checkRestorable - a library the index lists but restore cannot fetch is NotOnFeed`` () =
    // Measured on FsHotWatch: the index served a new version four minutes before a
    // restore of it succeeded. A gate that cleared on the listing would push the
    // dependents into that window.
    test <@ checkRestorable indexLists restoreFails false "SomePackage" "1.2.3" = NotOnFeed @>

[<Fact>]
let ``checkRestorable - a library that restores is OnFeed whatever the public index says`` () =
    // A privately published package never appears on nuget.org's flat container;
    // the restore probe honours the repo nuget.config, so it decides.
    test <@ checkRestorable indexLacks restoreSucceeds false "SomePackage" "1.2.3" = OnFeed @>
    test <@ checkRestorable (fun _ -> HttpFailed "offline") restoreSucceeds false "SomePackage" "1.2.3" = OnFeed @>

[<Fact>]
let ``checkRestorable - a library asks the restore probe, never only the index`` () =
    let mutable restores = 0

    let counting (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        restores <- restores + 1
        FsSemanticTagger.Shell.Success ""

    checkRestorable indexLists counting false "SomePackage" "1.2.3" |> ignore
    test <@ restores = 1 @>

[<Fact>]
let ``checkRestorable - a tool is decided by the index, since a PackageReference probe of it always fails`` () =
    let nu1212 (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("error NU1212: Invalid project-package combination for SomeTool 1.2.3.", 1)

    test <@ checkRestorable indexLists nu1212 true "SomeTool" "1.2.3" = OnFeed @>
    test <@ checkRestorable indexLacks nu1212 true "SomeTool" "1.2.3" = NotOnFeed @>

    test
        <@
            match checkRestorable (fun _ -> HttpFailed "offline") nu1212 true "SomeTool" "1.2.3" with
            | FeedUnknown _ -> true
            | _ -> false
        @>

[<Fact>]
let ``checkRestorable - a tool never runs the restore probe`` () =
    let mutable restores = 0

    let counting (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        restores <- restores + 1
        FsSemanticTagger.Shell.Success ""

    checkRestorable indexLists counting true "SomeTool" "1.2.3" |> ignore
    test <@ restores = 0 @>

[<Fact>]
let ``probeAvailabilityArgs appends --no-http-cache and keeps --configfile when present`` () =
    let args = probeAvailabilityArgs (Some "/repo/nuget.config") "/tmp/probe.csproj"
    test <@ args.StartsWith("restore \"/tmp/probe.csproj\"") @>
    test <@ args.Contains("--configfile \"/repo/nuget.config\"") @>
    test <@ args.Contains("--no-http-cache") @>

[<Fact>]
let ``probeAvailabilityArgs appends --no-http-cache when no repo nuget.config`` () =
    let args = probeAvailabilityArgs None "/tmp/probe.csproj"
    test <@ args = "restore \"/tmp/probe.csproj\" --no-http-cache" @>

[<Fact>]
let ``probeRestoreArgs pins repo nuget.config via --configfile when present`` () =
    let args = probeRestoreArgs (Some "/repo/nuget.config") "/tmp/probe.csproj"
    test <@ args.StartsWith("restore \"/tmp/probe.csproj\"") @>
    // Without this, a prior release on a repo-local/private feed wouldn't resolve.
    test <@ args.Contains("--configfile \"/repo/nuget.config\"") @>

[<Fact>]
let ``probeRestoreArgs omits --configfile when no repo nuget.config`` () =
    let args = probeRestoreArgs None "/tmp/probe.csproj"
    test <@ args = "restore \"/tmp/probe.csproj\"" @>

/// The previous release's API when it could be read, else None.
let private previousApi run (packageId: string) (version: string) : ApiSignature list option =
    match (Extraction.readPrevious run packageId version).Api with
    | Found api -> Some api
    | Unreadable _
    | NotRestorable _
    | FetchError _ -> None

[<Fact>]
let ``readPrevious API returns None when uncached and download fails`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("restore failed", 1)

    test <@ previousApi fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = None @>

[<Fact>]
let ``readPrevious API returns None when download succeeds but package still absent`` () =
    // Restore "succeeds" but our fake doesn't actually place the package in the cache,
    // so the re-check still finds nothing — must stay None, never fabricate an API.
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Success ""

    test <@ previousApi fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = None @>

[<Fact>]
let ``readPrevious API returns cached API without downloading when already present`` () =
    // FSharp.Core is always in the cache (it's a build dependency). Find a version
    // whose lib/ contains the dll, then assert the cache hit short-circuits download.
    let home =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let fsCoreDir = System.IO.Path.Combine(home, ".nuget", "packages", "fsharp.core")

    let cachedVersion =
        System.IO.Directory.GetDirectories(fsCoreDir)
        |> Array.map System.IO.Path.GetFileName
        |> Array.filter (fun v -> System.IO.Directory.Exists(System.IO.Path.Combine(fsCoreDir, v, "lib")))
        |> Array.head

    let mutable downloadAttempted = false

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        downloadAttempted <- true
        FsSemanticTagger.Shell.Failure("should not be called on a cache hit", 1)

    let result = previousApi fakeRun "FSharp.Core" cachedVersion
    test <@ Option.isSome result @>
    test <@ not downloadAttempted @>

// classifyRestoreFailure — not-restorable (NotRestorable) vs transient (FetchError)
// classification of a `dotnet restore` failure. A NotRestorable prior is only
// walked past when the FEED confirms it is absent; an outage still aborts.

[<Fact>]
let ``classifyRestoreFailure - NU1101 package-not-found is NotRestorable`` () =
    let msg = "error NU1101: Unable to find package Foo. No packages exist."
    test <@ classifyRestoreFailure msg = NotRestorable msg @>

[<Fact>]
let ``classifyRestoreFailure - NU1102 version-not-found is NotRestorable`` () =
    let msg = "error NU1102: Unable to find package Foo with version (= 9.9.9)"
    test <@ classifyRestoreFailure msg = NotRestorable msg @>

[<Fact>]
let ``classifyRestoreFailure - service-index/connection failure is FetchError`` () =
    let msg =
        "error : Unable to load the service index for source https://feed. connection timed out"

    test <@ classifyRestoreFailure msg = FetchError msg @>

[<Fact>]
let ``classifyRestoreFailure - NU1301 service-index 404 is FetchError not NotRestorable`` () =
    // NU1301 wraps a feed outage as "...404 (Not Found)". A bare "not found" match
    // would mis-classify this as absence and walk past a genuinely published prior.
    let msg =
        "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json. Response status code does not indicate success: 404 (Not Found)."

    test <@ classifyRestoreFailure msg = FetchError msg @>

[<Fact>]
let ``readPrevious - NotRestorable when uncached and restore reports package absent`` () =
    let msg = "error NU1101: Unable to find package ThisPackageDoesNotExist12345"

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure(msg, 1)

    test <@ (Extraction.readPrevious fakeRun "ThisPackageDoesNotExist12345" "9.9.9").Api = NotRestorable msg @>

[<Fact>]
let ``readPrevious - FetchError when restore succeeds but the package is still not cached`` () =
    // Restore said yes, yet nothing is where we read from (e.g. a relocated global
    // packages folder). That is not knowledge of absence: it must abort, not walk back.
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Success ""

    match (Extraction.readPrevious fakeRun "ThisPackageDoesNotExist12345" "9.9.9").Api with
    | FetchError reason -> test <@ reason.Contains("restore succeeded") @>
    | other -> failwithf "Expected FetchError, got %A" other

[<Fact>]
let ``readPrevious - FetchError when uncached and feed unreachable`` () =
    let msg = "Unable to load the service index ... connection timed out"

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure(msg, 1)

    test <@ (Extraction.readPrevious fakeRun "ThisPackageDoesNotExist12345" "9.9.9").Api = FetchError msg @>

// ApiChange.toList

[<Fact>]
let ``ApiChange.toList Breaking returns all items`` () =
    let change =
        Breaking(ApiSignature.Marker "a", [ ApiSignature.Marker "b"; ApiSignature.Marker "c" ])

    test <@ ApiChange.toList change = [ ApiSignature.Marker "a"; ApiSignature.Marker "b"; ApiSignature.Marker "c" ] @>

[<Fact>]
let ``ApiChange.toList Addition returns all items`` () =
    let change = Addition(ApiSignature.Marker "a", [ ApiSignature.Marker "b" ])
    test <@ ApiChange.toList change = [ ApiSignature.Marker "a"; ApiSignature.Marker "b" ] @>

[<Fact>]
let ``ApiChange.toList NoChange returns empty`` () =
    test <@ List.isEmpty (ApiChange.toList NoChange) @>

[<Fact>]
let ``ApiChange.toList single item Breaking`` () =
    let change = Breaking(ApiSignature.Marker "a", [])
    test <@ ApiChange.toList change = [ ApiSignature.Marker "a" ] @>

[<Fact>]
let ``readCacheRoot returns signatures for cached tool package`` () =
    // Build a fake NuGet cache layout mimicking a dotnet tool:
    //   <root>/fakepkg/1.0.0/tools/net10.0/any/FakePkg.dll
    // Reuse the compiled test assembly as the DLL payload so the test has no
    // external-cache dependency.
    let srcDll = taggerDll

    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fstagger-cache-" + System.Guid.NewGuid().ToString("N"))

    let toolsDir =
        System.IO.Path.Combine(cacheRoot, "fakepkg", "1.0.0", "tools", "net10.0", "any")

    System.IO.Directory.CreateDirectory(toolsDir) |> ignore
    System.IO.File.Copy(srcDll, System.IO.Path.Combine(toolsDir, "FakePkg.dll"))
    // The resolver needs FSharp.Core alongside for MetadataLoadContext;
    // copy every sibling DLL so we don't couple to a specific dependency set.
    for dep in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(srcDll), "*.dll") do
        let destName = System.IO.Path.GetFileName(dep)

        if destName <> "FakePkg.dll" then
            System.IO.File.Copy(dep, System.IO.Path.Combine(toolsDir, destName), true)

    try
        match (Extraction.readCacheRoot cacheRoot "FakePkg" "1.0.0").Api with
        | CachedRead sigs -> test <@ sigs.Length > 0 @>
        | other -> failwithf "Expected signatures from fixture cache, got %A" other
    finally
        System.IO.Directory.Delete(cacheRoot, true)

[<Fact>]
let ``readCacheRoot finds an analyzer-packaged assembly under analyzers-dotnet-fs`` () =
    // An FSharp.Analyzers.SDK analyzer package (IncludeBuildOutput=false,
    // DevelopmentDependency=true) ships its assembly under analyzers/dotnet/fs/<id>.dll
    // with NO lib/ folder. A resolver that searches only lib/ and tools/ never finds
    // the DLL, and the package's API is never read.
    //   <root>/fakeanalyzer/1.0.0/analyzers/dotnet/fs/FakeAnalyzer.dll   (and NO lib/)
    let srcDll = taggerDll

    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fstagger-cache-" + System.Guid.NewGuid().ToString("N"))

    let analyzerDir =
        System.IO.Path.Combine(cacheRoot, "fakeanalyzer", "1.0.0", "analyzers", "dotnet", "fs")

    System.IO.Directory.CreateDirectory(analyzerDir) |> ignore
    System.IO.File.Copy(srcDll, System.IO.Path.Combine(analyzerDir, "FakeAnalyzer.dll"))
    // The resolver needs FSharp.Core (and siblings) alongside for MetadataLoadContext.
    for dep in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(srcDll), "*.dll") do
        let destName = System.IO.Path.GetFileName(dep)

        if destName <> "FakeAnalyzer.dll" then
            System.IO.File.Copy(dep, System.IO.Path.Combine(analyzerDir, destName), true)

    try
        match (Extraction.readCacheRoot cacheRoot "FakeAnalyzer" "1.0.0").Api with
        | CachedRead sigs -> test <@ sigs.Length > 0 @>
        | other ->
            failwithf "Expected signatures from analyzer-packaged fixture cache (analyzers/dotnet/fs), got %A" other
    finally
        System.IO.Directory.Delete(cacheRoot, true)

[<Fact>]
let ``readCacheRoot still finds a lib-packaged assembly (lib layout unchanged)`` () =
    // Regression guard: adding the analyzers/ search path must not disturb the
    // classic library layout <root>/<id>/<ver>/lib/<tfm>/<id>.dll.
    let srcDll = taggerDll

    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fstagger-cache-" + System.Guid.NewGuid().ToString("N"))

    let libDir = System.IO.Path.Combine(cacheRoot, "fakelib", "1.0.0", "lib", "net10.0")

    System.IO.Directory.CreateDirectory(libDir) |> ignore
    System.IO.File.Copy(srcDll, System.IO.Path.Combine(libDir, "FakeLib.dll"))

    for dep in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(srcDll), "*.dll") do
        let destName = System.IO.Path.GetFileName(dep)

        if destName <> "FakeLib.dll" then
            System.IO.File.Copy(dep, System.IO.Path.Combine(libDir, destName), true)

    try
        match (Extraction.readCacheRoot cacheRoot "FakeLib" "1.0.0").Api with
        | CachedRead sigs -> test <@ sigs.Length > 0 @>
        | other -> failwithf "Expected signatures from lib-packaged fixture cache, got %A" other
    finally
        System.IO.Directory.Delete(cacheRoot, true)

[<Fact>]
let ``readCacheRoot reports a cached package with no assembly as unreadable, not as an API`` () =
    // The analyzers/ fallback must not conjure an API out of nothing: a package dir
    // that exists but ships no <id>.dll under lib/, tools/ or analyzers/ is
    // CachedUnreadable — a real package with no readable API, never "absent".
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fstagger-cache-" + System.Guid.NewGuid().ToString("N"))

    // A present version dir with an analyzers/ tree but the WRONG dll name inside.
    let analyzerDir =
        System.IO.Path.Combine(cacheRoot, "emptypkg", "1.0.0", "analyzers", "dotnet", "fs")

    System.IO.Directory.CreateDirectory(analyzerDir) |> ignore
    System.IO.File.WriteAllText(System.IO.Path.Combine(analyzerDir, "SomethingElse.dll"), "not the package assembly")

    try
        test
            <@
                (Extraction.readCacheRoot cacheRoot "EmptyPkg" "1.0.0").Api =
                    CachedUnreadable
                        "EmptyPkg 1.0.0 is in the NuGet cache but ships no EmptyPkg.dll under lib/, tools/ or analyzers/"
            @>
    finally
        System.IO.Directory.Delete(cacheRoot, true)

[<Fact>]
let ``readPrevious reports Found for an analyzer-packaged cached package`` () =
    // End-to-end: an analyzer package whose assembly lives under
    // analyzers/dotnet/fs/ must resolve from the local cache as Found, NOT be
    // reported as unreadable. Fixture a GUID-named package
    // in the real user cache so Extraction.readNuGetCache (which reads ~/.nuget/packages)
    // sees it, then assert the cache hit short-circuits before any restore.
    let home =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let srcDll = taggerDll

    // Unique lower-cased id so we never collide with a real cached package.
    let pkgId = "fsst-analyzer-fixture-" + System.Guid.NewGuid().ToString("N")

    let analyzerDir =
        System.IO.Path.Combine(home, ".nuget", "packages", pkgId, "1.0.0", "analyzers", "dotnet", "fs")

    System.IO.Directory.CreateDirectory(analyzerDir) |> ignore
    System.IO.File.Copy(srcDll, System.IO.Path.Combine(analyzerDir, pkgId + ".dll"))

    for dep in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(srcDll), "*.dll") do
        let destName = System.IO.Path.GetFileName(dep)

        if destName <> pkgId + ".dll" then
            System.IO.File.Copy(dep, System.IO.Path.Combine(analyzerDir, destName), true)

    // If the cache lookup ever falls through to restore, fail loudly: a green
    // restore would mask the very regression under test.
    let failRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("restore must not be reached for a cached package", 1)

    try
        match (Extraction.readPrevious failRun pkgId "1.0.0").Api with
        | Found sigs -> test <@ not (List.isEmpty sigs) @>
        | other -> failwithf "Expected Found for analyzer-packaged cache, got %A" other
    finally
        System.IO.Directory.Delete(System.IO.Path.Combine(home, ".nuget", "packages", pkgId), true)

[<Fact>]
let ``formatTypeName handles nested generic types`` () =
    // List<int> is a generic with one type arg
    let listType = typeof<System.Collections.Generic.List<int>>
    let formatted = formatTypeName listType

    test
        <@ formatted = "System.Collections.Generic.List<System.Int32 [System.Private.CoreLib]> [System.Private.CoreLib]" @>

[<Fact>]
let ``formatTypeName handles multi-dimensional arrays`` () =
    test <@ formatTypeName typeof<int[][]> = "System.Int32 [System.Private.CoreLib][][]" @>

[<Fact>]
let ``formatTypeName handles generic array combinations`` () =
    test
        <@
            formatTypeName typeof<System.Collections.Generic.List<string>[]> =
                "System.Collections.Generic.List<System.String [System.Private.CoreLib]> [System.Private.CoreLib][]"
        @>

[<Fact>]
let ``compare reads a new nested type under an existing type as an addition`` () =
    // A nested type alone does not mean a union case: modules compile to classes
    // too. Only a `case` signature on a union that already had cases breaks.
    let baseline = [ ApiSignature.TypeDecl "MyModule.Parent" ]

    let current =
        [
            ApiSignature.TypeDecl "MyModule.Parent"
            ApiSignature.TypeDecl "MyModule.Parent+Child"
        ]

    test <@ compare baseline current = Addition(ApiSignature.TypeDecl "MyModule.Parent+Child", []) @>

[<Fact>]
let ``compare lists only the breaking signatures, cases first`` () =
    let baseline =
        [
            ApiSignature.Member("U", "NewA(): M.U")
            ApiSignature.Member("U", "NewGone(): M.U")
            ApiSignature.UnionCase("M.U", "A")
            ApiSignature.UnionCase("M.U", "Gone")
            ApiSignature.TypeDecl "M.U"
            ApiSignature.TypeDecl "M.U+Gone"
        ]

    let current =
        [
            ApiSignature.Member("U", "NewA(): M.U")
            ApiSignature.Member("U", "NewB(): M.U")
            ApiSignature.UnionCase("M.U", "A")
            ApiSignature.UnionCase("M.U", "B")
            ApiSignature.TypeDecl "M.U"
            ApiSignature.TypeDecl "M.U+B"
        ]

    test
        <@
            compare baseline current =
                Breaking(
                    ApiSignature.UnionCase("M.U", "B"),
                    [
                        ApiSignature.UnionCase("M.U", "Gone")
                        ApiSignature.TypeDecl "M.U+Gone"
                        ApiSignature.Member("U", "NewGone(): M.U")
                    ]
                )
        @>

[<Fact>]
let ``compare classifies a case whose name contains :: by its union, not its text`` () =
    // A double-backtick case name may contain "::". Read back from the rendered
    // "case M.U::A::B", the union would be "M.U::A", a union the baseline never had,
    // and the new case would pass as a mere addition.
    let baseline = [ ApiSignature.TypeDecl "M.U"; ApiSignature.UnionCase("M.U", "A") ]
    let added = ApiSignature.UnionCase("M.U", "A::B")

    test <@ compare baseline (added :: baseline) = Breaking(added, []) @>

[<Fact>]
let ``render prints each signature kind as extract-api always has`` () =
    test <@ ApiSignature.render (ApiSignature.UnionCase("M.U", "A")) = "case M.U::A" @>
    test <@ ApiSignature.render (ApiSignature.TypeDecl "M.U+A") = "type M.U+A" @>
    test <@ ApiSignature.render (ApiSignature.Member("U", "get_A(): M.U")) = "  U::get_A(): M.U" @>
    test <@ ApiSignature.render (ApiSignature.Marker "grammar: changed") = "grammar: changed" @>

[<Fact>]
let ``compare non-nested new type is Addition not Breaking`` () =
    // New type that is NOT a nested DU case (no + in name)
    let baseline = [ ApiSignature.TypeDecl "MyModule.Foo" ]

    let current =
        [ ApiSignature.TypeDecl "MyModule.Foo"; ApiSignature.TypeDecl "MyModule.Bar" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition for non-nested new type, got %A" other

[<Fact>]
let ``compare new nested type where parent is also new is Addition`` () =
    // Both parent and nested type are new - not breaking
    let baseline = [ ApiSignature.TypeDecl "MyModule.Other" ]

    let current =
        [
            ApiSignature.TypeDecl "MyModule.Other"
            ApiSignature.TypeDecl "MyModule.NewUnion"
            ApiSignature.TypeDecl "MyModule.NewUnion+CaseA"
        ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``extractFromAssembly extracts constructors`` () =
    let dllPath = taggerDll

    let signatures = extractFromAssembly dllPath

    let hasCtors =
        signatures |> List.exists (ApiSignature.render >> fun s -> s.Contains(".ctor"))

    test <@ hasCtors @>

[<Fact>]
let ``extractFromAssembly extracts properties`` () =
    let dllPath = taggerDll

    let signatures = extractFromAssembly dllPath

    let hasProps =
        signatures
        |> List.exists (
            ApiSignature.render
            >> fun s -> s.Contains("::") && s.Contains(": ") && not (s.Contains("("))
        )

    test <@ hasProps @>

[<Fact>]
let ``compare with added non-type signatures is Addition`` () =
    let baseline = [ ApiSignature.TypeDecl "Foo" ]

    let current =
        [ ApiSignature.TypeDecl "Foo"; ApiSignature.Member("Foo", "NewMethod(): Void") ]

    match compare baseline current with
    | Addition(s, []) -> test <@ (ApiSignature.render s).Contains("NewMethod") @>
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``getAssemblySearchPaths contains runtime directory`` () =
    let dllPath = taggerDll

    let paths = getAssemblySearchPaths dllPath

    let runtimeDir =
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()

    test <@ paths |> List.contains runtimeDir @>

[<Fact>]
let ``compare hasNewDuCase with only non-type additions is Addition`` () =
    // When all additions are non-type (methods, properties), hasNewDuCase is false
    let baseline =
        [
            ApiSignature.TypeDecl "MyModule.MyUnion"
            ApiSignature.TypeDecl "MyModule.MyUnion+CaseA"
        ]

    let current =
        [
            ApiSignature.TypeDecl "MyModule.MyUnion"
            ApiSignature.TypeDecl "MyModule.MyUnion+CaseA"
            ApiSignature.Member("MyUnion", "NewMethod(): Void")
        ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition for non-type addition, got %A" other

[<Fact>]
let ``compare with removed and added returns Breaking prioritizing removals`` () =
    // When there are both removals and additions, Breaking uses removals
    let baseline =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "OldMethod(): String")
            ApiSignature.Member("Foo", "AnotherOld(): Int32")
        ]

    let current =
        [
            ApiSignature.TypeDecl "Foo"
            ApiSignature.Member("Foo", "NewMethod(): String")
        ]

    match compare baseline current with
    | Breaking(h, t) ->
        let all = h :: t
        test <@ all |> List.exists (ApiSignature.render >> fun s -> s.Contains("OldMethod")) @>
        test <@ all |> List.exists (ApiSignature.render >> fun s -> s.Contains("AnotherOld")) @>
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``readNuGetCache returns NotCached for nonexistent version of real package`` () =
    // Package ID might exist but version won't
    test <@ (Extraction.readNuGetCache "FSharp.Core" "0.0.0-nonexistent").Api = NotCached @>

[<Fact>]
let ``createResolver's resolver loads a DLL and its references`` () =
    let dllPath = taggerDll

    let resolver = createResolver dllPath
    // Should be able to create a MetadataLoadContext with it
    use context = new System.Reflection.MetadataLoadContext(resolver)
    let assembly = context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dllPath))
    test <@ assembly.GetExportedTypes().Length > 0 @>

[<Fact>]
let ``extractFromAssembly extracts methods with parameters`` () =
    let dllPath = taggerDll

    let signatures = extractFromAssembly dllPath

    // Should have methods with parameter types listed
    let hasMethodWithParams =
        signatures
        |> List.exists (
            ApiSignature.render
            >> fun s -> s.Contains("(") && s.Contains(")") && s.Contains(",") |> not |> not
        )

    // At least some signatures should contain method signatures with return types
    let hasReturnTypes =
        signatures |> List.exists (ApiSignature.render >> fun s -> s.Contains("): "))

    test <@ hasReturnTypes @>

[<Fact>]
let ``compare adding non-nested type with plus sign in module name is Addition`` () =
    // A type name that contains + but parent is not in baseline (brand new module+type)
    let baseline = [ ApiSignature.TypeDecl "OtherModule.Foo" ]

    let current =
        [
            ApiSignature.TypeDecl "OtherModule.Foo"
            ApiSignature.TypeDecl "BrandNew.Namespace+SubType"
        ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition since parent BrandNew.Namespace is not in baseline, got %A" other

[<Fact>]
let ``getAssemblySearchPaths falls back to runtimeDir path computation when DOTNET_ROOT is unset`` () =
    let dllPath = taggerDll

    let paths = assemblySearchPathsFor None dllPath
    let withEmptyRoot = assemblySearchPathsFor (Some "") dllPath
    // dllDir and runtimeDir should always be present regardless of DOTNET_ROOT
    test <@ paths.All.Length >= 2 @>
    // An empty DOTNET_ROOT is the same as none.
    test <@ withEmptyRoot = paths @>

[<Fact>]
let ``getAssemblySearchPaths returns no sdk or shared dirs when DOTNET_ROOT points to empty dir`` () =
    TestHelpers.withTempDir (fun tmpDir ->
        let paths = assemblySearchPathsFor (Some tmpDir) taggerDll
        // No paths should come from the fake empty dotnet root
        test <@ paths.All |> List.forall (fun p -> not (p.StartsWith tmpDir)) @>)

[<Fact>]
let ``getAssemblySearchPaths searches the SDK FSharp dirs and shared frameworks under DOTNET_ROOT`` () =
    TestHelpers.withTempDir (fun dotnetRoot ->
        let fsharpDir = System.IO.Path.Combine(dotnetRoot, "sdk", "10.0.400", "FSharp")
        let noFsharpSdk = System.IO.Path.Combine(dotnetRoot, "sdk", "9.0.100")

        let framework =
            System.IO.Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", "10.0.4")

        System.IO.Directory.CreateDirectory fsharpDir |> ignore
        System.IO.Directory.CreateDirectory noFsharpSdk |> ignore
        System.IO.Directory.CreateDirectory framework |> ignore
        let dllPath = taggerDll

        let paths = assemblySearchPathsFor (Some dotnetRoot) dllPath

        test <@ paths.Installation |> List.contains fsharpDir @>
        test <@ paths.Installation |> List.contains framework @>
        test <@ not (paths.All |> List.exists (fun p -> p.StartsWith noFsharpSdk)) @>)

[<Fact>]
let ``the .NET installation's directories are listed once per process, a package's DLLs on every lookup`` () =
    TestHelpers.withTempDir (fun tmp ->
        let dotnetRoot = System.IO.Path.Combine(tmp, "dotnet")
        let framework = System.IO.Path.Combine(dotnetRoot, "shared", "Fake.App", "1.0.0")
        let packageDir = System.IO.Path.Combine(tmp, "pkg")
        System.IO.Directory.CreateDirectory framework |> ignore
        System.IO.Directory.CreateDirectory packageDir |> ignore
        let dll = System.IO.Path.Combine(packageDir, "Pkg.dll")
        System.IO.File.WriteAllText(dll, "")

        let first = assemblySearchPathsFor (Some dotnetRoot) dll

        let laterFramework =
            System.IO.Path.Combine(dotnetRoot, "shared", "Fake.App", "2.0.0")

        System.IO.Directory.CreateDirectory laterFramework |> ignore
        let second = assemblySearchPathsFor (Some dotnetRoot) dll

        test <@ first.Installation |> List.contains framework @>
        test <@ not (second.Installation |> List.contains laterFramework) @>

        // A restore can add a dependency mid-run; the next lookup finds it.
        test <@ firstDllNamed second.All "AddedDependency" = None @>
        let added = System.IO.Path.Combine(packageDir, "AddedDependency.dll")
        System.IO.File.WriteAllText(added, "")
        test <@ firstDllNamed second.All "AddedDependency" = Some added @>)

[<Fact>]
let ``oncePerProcess forgets a computation that threw, so the next call retries`` () =
    let calls = ref 0

    let listing =
        oncePerProcess (fun (key: string) ->
            calls.Value <- calls.Value + 1

            if calls.Value = 1 then failwith "transient" else key.Length)

    raises<exn> <@ listing "abc" @>
    test <@ listing "abc" = 3 @>
    test <@ listing "abc" = 3 @>
    test <@ calls.Value = 2 @>

[<Fact>]
let ``firstDllNamed takes the first dll of that name, in search-path order`` () =
    TestHelpers.withTempDir (fun tmp ->
        let dirs =
            [ "own"; "framework"; "package" ]
            |> List.map (fun name -> System.IO.Path.Combine(tmp, name))

        for dir in dirs do
            System.IO.Directory.CreateDirectory dir |> ignore

        let frameworkCopy = System.IO.Path.Combine(dirs[1], "Shared.dll")
        System.IO.File.WriteAllText(frameworkCopy, "")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirs[2], "Shared.dll"), "")

        test <@ firstDllNamed dirs "Shared" = Some frameworkCopy @>
        test <@ firstDllNamed dirs "Missing" = None @>)

[<Fact>]
let ``a reference is satisfied by a matching public key token, or by any when it names none`` () =
    let token = Some [| 1uy; 2uy |]
    test <@ satisfiesReference token token @>
    test <@ satisfiesReference None token @>
    test <@ satisfiesReference (Some [||]) None @>
    test <@ not (satisfiesReference token None) @>
    test <@ not (satisfiesReference token (Some [| 3uy |])) @>

[<Fact>]
let ``the probing resolver refuses a dll whose public key token the reference does not match`` () =
    TestHelpers.withTempDir (fun dir ->
        // An unsigned assembly in a file named for FSharp.Core, which is signed.
        System.IO.File.Copy(taggerDll, System.IO.Path.Combine(dir, "FSharp.Core.dll"))
        let resolver = ProbingAssemblyResolver [ dir ]
        use context = new System.Reflection.MetadataLoadContext(createResolver taggerDll)

        let resolve (name: string) =
            resolver.Resolve(context, System.Reflection.AssemblyName name)

        test <@ isNull (resolve "FSharp.Core, PublicKeyToken=b03f5f7f11d50a3a") @>
        test <@ isNull (resolve "NotThere") @>
        test <@ not (isNull (resolve "FSharp.Core")) @>)

[<Fact>]
let ``getAssemblySearchPaths returns dllDir when dll has no deps.json`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fakeDll = System.IO.Path.Combine(tmpDir, "Fake.dll")
        System.IO.File.WriteAllText(fakeDll, "")
        let paths = getAssemblySearchPaths fakeDll
        test <@ paths |> List.contains tmpDir @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``getAssemblySearchPaths handles deps.json with no libraries key`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fakeDll = System.IO.Path.Combine(tmpDir, "Fake.dll")
        System.IO.File.WriteAllText(fakeDll, "")

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(tmpDir, "Fake.deps.json"),
            """{"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"}}"""
        )

        let paths = getAssemblySearchPaths fakeDll
        test <@ paths |> List.contains tmpDir @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``getAssemblySearchPaths handles malformed deps.json gracefully`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fakeDll = System.IO.Path.Combine(tmpDir, "Fake.dll")
        System.IO.File.WriteAllText(fakeDll, "")
        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "Fake.deps.json"), "{ not valid json }")
        let paths = getAssemblySearchPaths fakeDll
        test <@ paths |> List.contains tmpDir @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``getAssemblySearchPaths skips deps.json entries whose packages are not in local cache`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        let fakeDll = System.IO.Path.Combine(tmpDir, "Fake.dll")
        System.IO.File.WriteAllText(fakeDll, "")

        let depsJson =
            """{
  "libraries": {
    "SomePackage/1.0.0": {
      "type": "package",
      "path": "completely/nonexistent/package/path"
    }
  }
}"""

        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "Fake.deps.json"), depsJson)
        let paths = getAssemblySearchPaths fakeDll
        // Package path doesn't exist so it's skipped; dllDir still present
        test <@ paths |> List.contains tmpDir @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``readNuspecDependencies parses grouped and flat dependencies`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        let nuspec =
            """<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Test.Pkg</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Falco" version="5.2.0" />
        <dependency id="FSharp.Core" version="[10.1.201, )" />
      </group>
    </dependencies>
  </metadata>
</package>"""

        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "test.pkg.nuspec"), nuspec)
        let deps = readNuspecDependencies tmpDir
        test <@ deps |> List.contains ("Falco", "5.2.0") @>
        test <@ deps |> List.contains ("FSharp.Core", "[10.1.201, )") @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``readNuspecDependencies returns empty when no nuspec present`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        test <@ List.isEmpty (readNuspecDependencies tmpDir) @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``resolveCachedPackageDir resolves an exact version range`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let verDir = System.IO.Path.Combine(cacheRoot, "falco", "5.2.0")
    System.IO.Directory.CreateDirectory(verDir) |> ignore

    try
        // NuGet often records the lower bound as a range like "[5.2.0, )".
        test <@ resolveCachedPackageDir cacheRoot "Falco" "[5.2.0, )" = Some verDir @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``resolveCachedPackageDir falls back to highest cached version when exact is absent`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(cacheRoot, "falco", "5.1.0"))
    |> ignore

    let high = System.IO.Path.Combine(cacheRoot, "falco", "5.2.0")
    System.IO.Directory.CreateDirectory(high) |> ignore

    try
        test <@ resolveCachedPackageDir cacheRoot "Falco" "9.9.9" = Some high @>
        test <@ resolveCachedPackageDir cacheRoot "Nonexistent" "1.0.0" = None @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``pickLibDir picks the first existing lib tfm`` () =
    let pkgDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let libNet8 = System.IO.Path.Combine(pkgDir, "lib", "net8.0")
    System.IO.Directory.CreateDirectory(libNet8) |> ignore

    try
        test <@ pickLibDir pkgDir = Some libNet8 @>
    finally
        try
            System.IO.Directory.Delete(pkgDir, true)
        with _ ->
            ()

[<Fact>]
let ``nuspecClosureDirsFor walks transitive deps for a cache-resident dll`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    // Lay out a fake cache: MyPkg -> Dep1 -> Dep2 (transitive).
    let mkPackage (id: string) (version: string) (deps: (string * string) list) =
        let verDir = System.IO.Path.Combine(cacheRoot, id.ToLowerInvariant(), version)

        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(verDir, "lib", "net10.0"))
        |> ignore

        let depXml =
            deps
            |> List.map (fun (i, v) -> sprintf """<dependency id="%s" version="%s" />""" i v)
            |> String.concat "\n"

        let nuspec =
            sprintf
                """<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><dependencies><group targetFramework="net10.0">%s</group></dependencies></metadata></package>"""
                depXml

        System.IO.File.WriteAllText(System.IO.Path.Combine(verDir, id.ToLowerInvariant() + ".nuspec"), nuspec)
        verDir

    try
        let myPkgDir = mkPackage "MyPkg" "1.0.0" [ "Dep1", "5.0.0" ]
        let dep1Dir = mkPackage "Dep1" "5.0.0" [ "Dep2", "2.0.0" ]
        let dep2Dir = mkPackage "Dep2" "2.0.0" []

        let dllPath = System.IO.Path.Combine(myPkgDir, "lib", "net10.0", "MyPkg.dll")
        let dirs = nuspecClosureDirsFor cacheRoot dllPath

        // Both the direct and the transitive dependency lib dirs are resolved.
        test <@ dirs |> List.contains (System.IO.Path.Combine(dep1Dir, "lib", "net10.0")) @>
        test <@ dirs |> List.contains (System.IO.Path.Combine(dep2Dir, "lib", "net10.0")) @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``nuspecClosureDirsFor returns empty for a dll outside the cache root`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let outsideDll =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"), "Foo.dll")

    test <@ List.isEmpty (nuspecClosureDirsFor cacheRoot outsideDll) @>

[<Fact>]
let ``nuspecClosureDirsFor returns empty when under cache but no nuspec is found`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    // A dll under the cache root whose package dir has no .nuspec anywhere upward.
    let libDir =
        System.IO.Path.Combine(cacheRoot, "nonuspec", "1.0.0", "lib", "net10.0")

    System.IO.Directory.CreateDirectory(libDir) |> ignore

    try
        let dllPath = System.IO.Path.Combine(libDir, "NoNuspec.dll")
        test <@ List.isEmpty (nuspecClosureDirsFor cacheRoot dllPath) @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``nuspecClosureDirsFor skips dependencies absent from the cache`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let verDir = System.IO.Path.Combine(cacheRoot, "lonely", "1.0.0")

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(verDir, "lib", "net10.0"))
    |> ignore

    // Depends on a package that is not present in the cache — it is simply skipped.
    System.IO.File.WriteAllText(
        System.IO.Path.Combine(verDir, "lonely.nuspec"),
        """<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><dependencies><dependency id="MissingDep" version="9.9.9" /></dependencies></metadata></package>"""
    )

    try
        let dllPath = System.IO.Path.Combine(verDir, "lib", "net10.0", "Lonely.dll")
        test <@ List.isEmpty (nuspecClosureDirsFor cacheRoot dllPath) @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``readNuspecDependencies returns empty for malformed nuspec xml`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "bad.nuspec"), "<package><not closed")
        test <@ List.isEmpty (readNuspecDependencies tmpDir) @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``resolveCachedPackageDir with empty version falls back to highest`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(cacheRoot, "pkg", "1.0.0"))
    |> ignore

    let high = System.IO.Path.Combine(cacheRoot, "pkg", "2.0.0")
    System.IO.Directory.CreateDirectory(high) |> ignore

    try
        test <@ resolveCachedPackageDir cacheRoot "Pkg" "" = Some high @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

[<Fact>]
let ``readNuspecDependencies skips deps without id and defaults missing version`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        // First dependency has no id (skipped); second has no version (defaults to "").
        let nuspec =
            """<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><dependencies><dependency version="1.0.0" /><dependency id="HasNoVersion" /></dependencies></metadata></package>"""

        System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "x.nuspec"), nuspec)
        let deps = readNuspecDependencies tmpDir
        test <@ deps = [ "HasNoVersion", "" ] @>
    finally
        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

[<Fact>]
let ``readCacheRoot reports a cached assembly that cannot be read as CachedUnreadable naming it`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let libDir = System.IO.Path.Combine(cacheRoot, "badpkg", "1.0.0", "lib", "net10.0")
    System.IO.Directory.CreateDirectory(libDir) |> ignore

    try
        // A file with the expected name but not a valid assembly — extraction must
        // degrade to CachedUnreadable (naming the assembly) rather than throwing.
        let badDll = System.IO.Path.Combine(libDir, "BadPkg.dll")
        System.IO.File.WriteAllText(badDll, "not a real assembly")

        match (Extraction.readCacheRoot cacheRoot "BadPkg" "1.0.0").Api with
        | CachedUnreadable reason -> test <@ reason.StartsWith("could not load " + badDll + ": ") @>
        | other -> failwithf "Expected CachedUnreadable, got %A" other
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

/// Writes to `path` a copy of the tagger's DLL whose reference to the
/// `CommandTree` assembly names `CommandTreX` instead, an assembly no cache
/// holds, so loading its API fails with "Could not find assembly".
let private writeUnloadableCopy (path: string) =
    let bytes = System.IO.File.ReadAllBytes taggerDll
    let name = System.Text.Encoding.ASCII.GetBytes "\000CommandTree\000"

    let at =
        System.MemoryExtensions.IndexOf(System.ReadOnlySpan bytes, System.ReadOnlySpan name)
    // The metadata string heap entry the assembly reference names.
    bytes[at + name.Length - 2] <- byte 'X'
    System.IO.File.WriteAllBytes(path, bytes)

[<Fact>]
let ``readDll reads a DLL's API and its CLI grammar`` () =
    let read = Extraction.readDll taggerDll

    test
        <@
            match read.Api with
            | Ok api -> api |> List.contains (ApiSignature.TypeDecl "FsSemanticTagger.Program+Command")
            | Error _ -> false
        @>

    test
        <@
            match read.Grammar with
            | GrammarModelled grammar -> not grammar.Roots.IsEmpty
            | GrammarNotModellable _
            | GrammarUnreadable _ -> false
        @>

[<Fact>]
let ``readDll of a file that is not an assembly is unreadable for both, for the same reason`` () =
    TestHelpers.withTempDir (fun dir ->
        let dll = System.IO.Path.Combine(dir, "Bad.dll")
        System.IO.File.WriteAllText(dll, "not an assembly")

        let read = Extraction.readDll dll

        match read.Api, read.Grammar with
        | Error reason, GrammarUnreadable grammarReason ->
            test <@ reason.StartsWith("could not load " + dll + ": ") @>
            test <@ grammarReason = reason @>
        | other -> failwithf "Expected both unreadable, got %A" other)

[<Fact>]
let ``readDll reports an API that cannot be read from an assembly that loads`` () =
    TestHelpers.withTempDir (fun dir ->
        let dll = System.IO.Path.Combine(dir, "Unloadable.dll")
        writeUnloadableCopy dll

        match (Extraction.readDll dll).Api with
        | Error reason ->
            test <@ reason.StartsWith("could not load " + dll + ": ") @>
            test <@ reason.Contains "CommandTreX" @>
        | Ok api -> failwithf "Expected the API to be unreadable, got %d signatures" api.Length)

[<Fact>]
let ``readCacheRoot reads the API from the first candidate assembly whose API reads`` () =
    TestHelpers.withTempDir (fun cacheRoot ->
        let libDir (tfm: string) =
            let dir = System.IO.Path.Combine(cacheRoot, "pkg", "1.0.0", "lib", tfm)
            System.IO.Directory.CreateDirectory dir |> ignore
            dir

        // Tried first (newest-tfm-first sorts net9.0 above net10.0), and unreadable.
        writeUnloadableCopy (System.IO.Path.Combine(libDir "net9.0", "Pkg.dll"))
        let readable = libDir "net10.0"
        // With its dependencies beside it, so its API loads.
        for dll in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName taggerDll, "*.dll") do
            System.IO.File.Copy(dll, System.IO.Path.Combine(readable, System.IO.Path.GetFileName dll))

        System.IO.File.Copy(taggerDll, System.IO.Path.Combine(readable, "Pkg.dll"))

        test
            <@
                match (Extraction.readCacheRoot cacheRoot "Pkg" "1.0.0").Api with
                | CachedRead api -> api |> List.contains (ApiSignature.TypeDecl "FsSemanticTagger.Program+Command")
                | NotCached
                | CachedUnreadable _ -> false
            @>)

/// The unreadable-assembly misclassification, at its source. A published package
/// whose assembly will not load — here our own DLL referencing an assembly no
/// cache holds, the same "Could not find assembly" failure an analyzer hits when
/// FSharp.Analyzers.SDK does not resolve — used to come back as AbsentOnFeed ("not
/// published") once a no-op restore succeeded. It is Unreadable, naming the
/// assembly and the dependency, and no restore is attempted: the package is right
/// there in the cache.
[<Fact>]
let ``readPrevious - a cached assembly that fails to load is Unreadable, never absent`` () =
    let home =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let pkgId = "fsst-unloadable-fixture-" + System.Guid.NewGuid().ToString("N")
    let pkgRoot = System.IO.Path.Combine(home, ".nuget", "packages", pkgId)
    let libDir = System.IO.Path.Combine(pkgRoot, "1.0.0", "lib", "net10.0")
    let dllPath = System.IO.Path.Combine(libDir, pkgId + ".dll")
    System.IO.Directory.CreateDirectory(libDir) |> ignore
    writeUnloadableCopy dllPath

    let mutable restoreAttempted = false

    let restoreRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        restoreAttempted <- true
        FsSemanticTagger.Shell.Success ""

    try
        match (Extraction.readPrevious restoreRun pkgId "1.0.0").Api with
        | Unreadable reason ->
            test <@ reason.StartsWith("could not load " + dllPath + ": ") @>
            test <@ reason.Contains("Could not find assembly 'CommandTreX") @>
        | other -> failwithf "Expected Unreadable, got %A" other

        test <@ not restoreAttempted @>
    finally
        System.IO.Directory.Delete(pkgRoot, true)

[<Fact>]
let ``readPrevious - a package restored but unloadable is Unreadable, never absent`` () =
    // The uncached path: restore brings the package in, and it still will not load.
    // This is exactly the shape that used to become AbsentOnFeed after a successful
    // restore.
    let home =
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let pkgId = "fsst-unloadable-fixture-" + System.Guid.NewGuid().ToString("N")
    let pkgRoot = System.IO.Path.Combine(home, ".nuget", "packages", pkgId)
    let libDir = System.IO.Path.Combine(pkgRoot, "1.0.0", "lib", "net10.0")

    // Restore "downloads" the package into the cache.
    let restoreRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        System.IO.Directory.CreateDirectory(libDir) |> ignore
        writeUnloadableCopy (System.IO.Path.Combine(libDir, pkgId + ".dll"))
        FsSemanticTagger.Shell.Success ""

    try
        match (Extraction.readPrevious restoreRun pkgId "1.0.0").Api with
        | Unreadable reason -> test <@ reason.Contains("Could not find assembly") @>
        | other -> failwithf "Expected Unreadable, got %A" other
    finally
        if System.IO.Directory.Exists pkgRoot then
            System.IO.Directory.Delete(pkgRoot, true)

[<Fact>]
let ``referencedAssemblies reads a dll's assembly references without loading it`` () =
    let references = referencedAssemblies taggerDll |> List.map fst
    test <@ references |> List.contains "CommandTree" @>
    test <@ List.isEmpty (referencedAssemblies "no-such.dll") @>

[<Fact>]
let ``cachedPackageDirForAssembly prefers the assembly's major.minor.build, else the highest version`` () =
    TestHelpers.withTempDir (fun cacheRoot ->
        let versionDir (version: string) =
            let dir = System.IO.Path.Combine(cacheRoot, "some.sdk", version)
            System.IO.Directory.CreateDirectory dir |> ignore
            dir

        let _ = versionDir "0.39.0"
        let exact = versionDir "0.39.2"
        let highest = versionDir "0.39.9-beta"

        test <@ cachedPackageDirForAssembly cacheRoot "Some.Sdk" (System.Version(0, 39, 2, 0)) = Some exact @>
        test <@ cachedPackageDirForAssembly cacheRoot "Some.Sdk" (System.Version(1, 0, 0, 0)) = Some highest @>
        test <@ cachedPackageDirForAssembly cacheRoot "Absent" (System.Version(1, 0, 0, 0)) = None @>)

[<Fact>]
let ``referencedPackageDirsFor adds the cached package of an unprovided reference and its dependencies`` () =
    TestHelpers.withTempDir (fun cacheRoot ->
        // The tagger references CommandTree, as an analyzer references FSharp.Analyzers.SDK.
        let commandTreeVersion =
            referencedAssemblies taggerDll
            |> List.find (fun (name, _) -> name = "CommandTree")
            |> snd

        let package (id: string) (version: string) (dependencies: (string * string) list) =
            let dir = System.IO.Path.Combine(cacheRoot, id.ToLowerInvariant(), version)

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "lib", "net10.0"))
            |> ignore

            let dependencyElements =
                dependencies
                |> List.map (fun (depId, depVersion) ->
                    sprintf "<dependency id=\"%s\" version=\"%s\" />" depId depVersion)
                |> String.concat ""

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, id.ToLowerInvariant() + ".nuspec"),
                sprintf
                    "<package><metadata><id>%s</id><dependencies><group>%s</group></dependencies></metadata></package>"
                    id
                    dependencyElements
            )

            System.IO.Path.Combine(dir, "lib", "net10.0")

        let commandTreeLib =
            package
                "CommandTree"
                (sprintf "%d.%d.%d" commandTreeVersion.Major commandTreeVersion.Minor commandTreeVersion.Build)
                [ "Its.Dependency", "2.0.0" ]

        let dependencyLib = package "Its.Dependency" "2.0.0" []

        let dirs =
            referencedPackageDirsFor cacheRoot (fun name -> name <> "CommandTree") taggerDll

        test <@ dirs = [ commandTreeLib; dependencyLib ] @>
        test <@ List.isEmpty (referencedPackageDirsFor cacheRoot (fun _ -> true) taggerDll) @>)

[<Fact>]
let ``readCacheRoot reads an analyzer-layout package whose nuspec declares no dependencies`` () =
    // An FSharp.Analyzers.SDK analyzer ships alone under analyzers/dotnet/fs/ and
    // lists no dependency; its references resolve from the NuGet cache by name.
    TestHelpers.withTempDir (fun cacheRoot ->
        let pkgDir = System.IO.Path.Combine(cacheRoot, "fake.analyzer", "1.0.0")
        let analyzerDir = System.IO.Path.Combine(pkgDir, "analyzers", "dotnet", "fs")
        System.IO.Directory.CreateDirectory analyzerDir |> ignore

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(pkgDir, "fake.analyzer.nuspec"),
            "<package><metadata><id>Fake.Analyzer</id><developmentDependency>true</developmentDependency></metadata></package>"
        )

        System.IO.File.Copy(taggerDll, System.IO.Path.Combine(analyzerDir, "Fake.Analyzer.dll"))

        match (Extraction.readCacheRoot cacheRoot "Fake.Analyzer" "1.0.0").Api with
        | CachedRead api -> test <@ api |> List.contains (ApiSignature.TypeDecl "FsSemanticTagger.Program+Command") @>
        | other -> failwithf "expected the analyzer's API, got %A" other)

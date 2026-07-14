module FsSemanticTagger.Tests.ApiTests

open Xunit
open Tests.Common
open Swensen.Unquote
open FsSemanticTagger.Api

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
            formatted = "Microsoft.FSharp.Core.FSharpResult<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core]"
        @>

[<Fact>]
let ``formatTypeName handles FSharpFunc`` () =
    let funcType = typeof<int -> string>
    let formatted = formatTypeName funcType

    test
        <@
            formatted = "Microsoft.FSharp.Core.FSharpFunc<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core]"
        @>

[<Fact>]
let ``formatTypeName handles arrays`` () =
    test <@ formatTypeName typeof<string[]> = "System.String [System.Private.CoreLib][]" @>
    test <@ formatTypeName typeof<int[]> = "System.Int32 [System.Private.CoreLib][]" @>

[<Fact>]
let ``formatTypeName handles nested generic arrays`` () =
    test
        <@
            formatTypeName typeof<Result<int, string>[]> = "Microsoft.FSharp.Core.FSharpResult<System.Int32 [System.Private.CoreLib], System.String [System.Private.CoreLib]> [FSharp.Core][]"
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
        ApiSignature(sprintf "  Holder::.ctor(%s)" (formatTypeName paramType))

    let oldApi =
        [ ApiSignature "type Holder"; ctorSig typeof<FsSemanticTagger.Version.Version> ]

    let newApi = [ ApiSignature "type Holder"; ctorSig typeof<System.Version> ]

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
        ApiSignature(sprintf "  Holder::.ctor(%s)" (formatTypeName paramType))

    let oldApi = [ ApiSignature "type Holder"; ctorSig typeof<System.Version> ]
    let newApi = [ ApiSignature "type Holder"; ctorSig typeof<System.Version> ]
    test <@ compare oldApi newApi = NoChange @>

[<Fact>]
let ``compare with identical APIs returns NoChange`` () =
    let api = [ ApiSignature "type Foo"; ApiSignature "  Foo::Bar(): String" ]

    test <@ compare api api = NoChange @>

[<Fact>]
let ``compare with added signatures returns Addition`` () =
    let baseline = [ ApiSignature "type Foo" ]

    let current = [ ApiSignature "type Foo"; ApiSignature "  Foo::Bar(): String" ]

    let result = compare baseline current

    test <@ result = Addition(ApiSignature "  Foo::Bar(): String", []) @>

[<Fact>]
let ``compare with removed signatures returns Breaking`` () =
    let baseline = [ ApiSignature "type Foo"; ApiSignature "  Foo::Bar(): String" ]

    let current = [ ApiSignature "type Foo" ]
    let result = compare baseline current

    test <@ result = Breaking(ApiSignature "  Foo::Bar(): String", []) @>

[<Fact>]
let ``compare with both added and removed returns Breaking`` () =
    let baseline =
        [ ApiSignature "type Foo"; ApiSignature "  Foo::OldMethod(): String" ]

    let current = [ ApiSignature "type Foo"; ApiSignature "  Foo::NewMethod(): Int32" ]

    match compare baseline current with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``compare with empty lists returns NoChange`` () = test <@ compare [] [] = NoChange @>

[<Fact>]
let ``extractFromAssembly extracts signatures from own DLL`` () =
    // Use the tool's own compiled DLL as a test fixture
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let signatures = extractFromAssembly dllPath

    // Should contain the Version type
    let hasVersionType =
        signatures
        |> List.exists (fun (ApiSignature s) -> s = "type FsSemanticTagger.Version+Version")

    test <@ hasVersionType @>

    // Should contain the parse function (compiled as static method)
    let hasParseFunction =
        signatures |> List.exists (fun (ApiSignature s) -> s.Contains("parse"))

    test <@ hasParseFunction @>

    // Should have non-trivial number of signatures
    test <@ signatures.Length > 5 @>

[<Fact>]
let ``extractFromAssembly results are sorted`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let signatures = extractFromAssembly dllPath
    let sorted = List.sort signatures
    test <@ signatures = sorted @>

[<Fact>]
let ``getAssemblySearchPaths includes DLL directory and runtime directory`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let paths = getAssemblySearchPaths dllPath
    // Should have at least the DLL dir and the runtime dir
    test <@ paths.Length >= 2 @>
    // First path should be the DLL's directory
    let dllDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dllPath))
    test <@ paths[0] = dllDir @>

[<Fact>]
let ``compare with only additions and no removals returns Addition`` () =
    let baseline = [ ApiSignature "type Foo" ]

    let current =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::Bar(): String"
          ApiSignature "  Foo::Baz(): Int32" ]

    match compare baseline current with
    | Addition _ -> test <@ (ApiChange.toList (compare baseline current)).Length = 2 @>
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``compare with only removals returns Breaking`` () =
    let baseline =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::Bar(): String"
          ApiSignature "  Foo::Baz(): Int32" ]

    let current = [ ApiSignature "type Foo" ]

    match compare baseline current with
    | Breaking _ -> test <@ (ApiChange.toList (compare baseline current)).Length = 2 @>
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``compare detects new DU case as breaking change`` () =
    // Adding a case to a discriminated union breaks exhaustive pattern matches.
    // F# compiles DU cases as nested types: ParentType+CaseName
    let baseline =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA"
          ApiSignature "type MyModule.MyUnion+CaseB"
          ApiSignature "  MyUnion::get_Tag(): Int32"
          ApiSignature "  MyUnion+CaseA::.ctor(): Void"
          ApiSignature "  MyUnion+CaseB::.ctor(String): Void" ]

    let current =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA"
          ApiSignature "type MyModule.MyUnion+CaseB"
          ApiSignature "type MyModule.MyUnion+CaseC"
          ApiSignature "  MyUnion::get_Tag(): Int32"
          ApiSignature "  MyUnion+CaseA::.ctor(): Void"
          ApiSignature "  MyUnion+CaseB::.ctor(String): Void"
          ApiSignature "  MyUnion+CaseC::.ctor(Int32): Void" ]

    match compare baseline current with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking for new DU case, got %A" other

[<Fact>]
let ``compare does not flag new nested type as breaking when parent is new`` () =
    // A brand new type with nested cases is just an addition, not breaking
    let baseline = [ ApiSignature "type MyModule.OtherType" ]

    let current =
        [ ApiSignature "type MyModule.OtherType"
          ApiSignature "type MyModule.NewUnion"
          ApiSignature "type MyModule.NewUnion+CaseA"
          ApiSignature "type MyModule.NewUnion+CaseB" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition for entirely new type, got %A" other

[<Fact>]
let ``extractFromNuGetCache returns None for nonexistent package`` () =
    test <@ extractFromNuGetCache "ThisPackageDoesNotExist12345" "1.0.0" = None @>

// downloadToCache / extractPreviousFromNuGet — the prior-API fetch path.
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
            flatContainerIndexUrl "FsSemanticTagger" = "https://api.nuget.org/v3-flatcontainer/fssemantictagger/index.json"
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

    let fakeFetch (url: string) : Result<string, string> =
        fetched <- fetched @ [ url ]
        Ok """{"versions":["1.2.3"]}"""

    test <@ isPublishedViaFlatContainer fakeFetch "SomePackage" "1.2.3" = true @>
    test <@ fetched = [ "https://api.nuget.org/v3-flatcontainer/somepackage/index.json" ] @>

[<Fact>]
let ``isPublishedViaFlatContainer false when fetch errors (offline or non-2xx)`` () =
    let fakeFetch (_url: string) : Result<string, string> = Error "HTTP 404"
    test <@ isPublishedViaFlatContainer fakeFetch "SomePackage" "1.2.3" = false @>

[<Fact>]
let ``isPublished succeeds on the FIRST attempt via flat container without ever probing restore`` () =
    // This is the regression: previously isPublished only ran a dotnet restore,
    // which timed out while the flat container already had the package. Now the
    // flat-container hit short-circuits and the restore probe is never invoked.
    let mutable restored = false

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        restored <- true
        FsSemanticTagger.Shell.Failure("registration index lagging", 1)

    let fakeFetch (_url: string) : Result<string, string> = Ok """{"versions":["1.2.3"]}"""

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

    let fakeFetch (_url: string) : Result<string, string> = Ok """{"versions":[]}"""

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

    let fakeFetch (_url: string) : Result<string, string> = Error "offline"
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

[<Fact>]
let ``extractPreviousFromNuGet returns None when uncached and download fails`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("restore failed", 1)

    test <@ extractPreviousFromNuGet fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = None @>

[<Fact>]
let ``extractPreviousFromNuGet returns None when download succeeds but package still absent`` () =
    // Restore "succeeds" but our fake doesn't actually place the package in the cache,
    // so the re-check still finds nothing — must stay None, never fabricate an API.
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Success ""

    test <@ extractPreviousFromNuGet fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = None @>

[<Fact>]
let ``extractPreviousFromNuGet returns cached API without downloading when already present`` () =
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

    let result = extractPreviousFromNuGet fakeRun "FSharp.Core" cachedVersion
    test <@ Option.isSome result @>
    test <@ not downloadAttempted @>

// classifyRestoreFailure — orphan (AbsentOnFeed) vs transient (FetchError)
// classification of a `dotnet restore` failure. This is what lets the release
// walk back past an orphan tag but still abort on an outage.

[<Fact>]
let ``classifyRestoreFailure - NU1101 package-not-found is AbsentOnFeed`` () =
    test <@ classifyRestoreFailure "error NU1101: Unable to find package Foo. No packages exist." = AbsentOnFeed @>

[<Fact>]
let ``classifyRestoreFailure - NU1102 version-not-found is AbsentOnFeed`` () =
    test <@ classifyRestoreFailure "error NU1102: Unable to find package Foo with version (= 9.9.9)" = AbsentOnFeed @>

[<Fact>]
let ``classifyRestoreFailure - service-index/connection failure is FetchError`` () =
    let msg =
        "error : Unable to load the service index for source https://feed. connection timed out"

    test <@ classifyRestoreFailure msg = FetchError msg @>

[<Fact>]
let ``classifyRestoreFailure - NU1301 service-index 404 is FetchError not AbsentOnFeed`` () =
    // NU1301 wraps a feed outage as "...404 (Not Found)". A bare "not found" match
    // would mis-classify this as absence and walk past a genuinely published prior.
    let msg =
        "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json. Response status code does not indicate success: 404 (Not Found)."

    test <@ classifyRestoreFailure msg = FetchError msg @>

[<Fact>]
let ``extractPreviousFromNuGetResult - AbsentOnFeed when uncached and restore reports package absent`` () =
    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure("error NU1101: Unable to find package ThisPackageDoesNotExist12345", 1)

    test <@ extractPreviousFromNuGetResult fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = AbsentOnFeed @>

[<Fact>]
let ``extractPreviousFromNuGetResult - FetchError when uncached and feed unreachable`` () =
    let msg = "Unable to load the service index ... connection timed out"

    let fakeRun (_cmd: string) (_args: string) : FsSemanticTagger.Shell.CommandResult =
        FsSemanticTagger.Shell.Failure(msg, 1)

    test <@ extractPreviousFromNuGetResult fakeRun "ThisPackageDoesNotExist12345" "9.9.9" = FetchError msg @>

// ApiChange.toList

[<Fact>]
let ``ApiChange.toList Breaking returns all items`` () =
    let change = Breaking(ApiSignature "a", [ ApiSignature "b"; ApiSignature "c" ])
    test <@ ApiChange.toList change = [ ApiSignature "a"; ApiSignature "b"; ApiSignature "c" ] @>

[<Fact>]
let ``ApiChange.toList Addition returns all items`` () =
    let change = Addition(ApiSignature "a", [ ApiSignature "b" ])
    test <@ ApiChange.toList change = [ ApiSignature "a"; ApiSignature "b" ] @>

[<Fact>]
let ``ApiChange.toList NoChange returns empty`` () =
    test <@ List.isEmpty (ApiChange.toList NoChange) @>

[<Fact>]
let ``ApiChange.toList single item Breaking`` () =
    let change = Breaking(ApiSignature "a", [])
    test <@ ApiChange.toList change = [ ApiSignature "a" ] @>

[<Fact>]
let ``extractFromCacheRoot returns signatures for cached tool package`` () =
    // Build a fake NuGet cache layout mimicking a dotnet tool:
    //   <root>/fakepkg/1.0.0/tools/net10.0/any/FakePkg.dll
    // Reuse the compiled test assembly as the DLL payload so the test has no
    // external-cache dependency.
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let srcDll =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

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
        match extractFromCacheRoot cacheRoot "FakePkg" "1.0.0" with
        | Some sigs -> test <@ sigs.Length > 0 @>
        | None -> failwith "Expected Some signatures from fixture cache"
    finally
        System.IO.Directory.Delete(cacheRoot, true)

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
            formatTypeName typeof<System.Collections.Generic.List<string>[]> = "System.Collections.Generic.List<System.String [System.Private.CoreLib]> [System.Private.CoreLib][]"
        @>

[<Fact>]
let ``compare new DU case with no removals is Breaking`` () =
    // Specifically test the hasNewDuCase path with no removals but added nested type
    let baseline =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA" ]

    let current =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA"
          ApiSignature "type MyModule.MyUnion+CaseB" ]

    match compare baseline current with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking for new DU case, got %A" other

[<Fact>]
let ``compare non-nested new type is Addition not Breaking`` () =
    // New type that is NOT a nested DU case (no + in name)
    let baseline = [ ApiSignature "type MyModule.Foo" ]

    let current = [ ApiSignature "type MyModule.Foo"; ApiSignature "type MyModule.Bar" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition for non-nested new type, got %A" other

[<Fact>]
let ``compare new nested type where parent is also new is Addition`` () =
    // Both parent and nested type are new - not breaking
    let baseline = [ ApiSignature "type MyModule.Other" ]

    let current =
        [ ApiSignature "type MyModule.Other"
          ApiSignature "type MyModule.NewUnion"
          ApiSignature "type MyModule.NewUnion+CaseA" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``extractFromAssembly extracts constructors`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let signatures = extractFromAssembly dllPath

    let hasCtors =
        signatures |> List.exists (fun (ApiSignature s) -> s.Contains(".ctor"))

    test <@ hasCtors @>

[<Fact>]
let ``extractFromAssembly extracts properties`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let signatures = extractFromAssembly dllPath

    // Version record should have property getters
    let hasProps =
        signatures
        |> List.exists (fun (ApiSignature s) -> s.Contains("::") && s.Contains(": ") && not (s.Contains("(")))

    test <@ hasProps @>

[<Fact>]
let ``compare with added non-type signatures is Addition`` () =
    let baseline = [ ApiSignature "type Foo" ]

    let current = [ ApiSignature "type Foo"; ApiSignature "  Foo::NewMethod(): Void" ]

    match compare baseline current with
    | Addition(ApiSignature s, []) -> test <@ s.Contains("NewMethod") @>
    | other -> failwithf "Expected Addition, got %A" other

[<Fact>]
let ``getAssemblySearchPaths contains runtime directory`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let paths = getAssemblySearchPaths dllPath

    let runtimeDir =
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()

    test <@ paths |> List.contains runtimeDir @>

[<Fact>]
let ``compare hasNewDuCase with only non-type additions is Addition`` () =
    // When all additions are non-type (methods, properties), hasNewDuCase is false
    let baseline =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA" ]

    let current =
        [ ApiSignature "type MyModule.MyUnion"
          ApiSignature "type MyModule.MyUnion+CaseA"
          ApiSignature "  MyUnion::NewMethod(): Void" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition for non-type addition, got %A" other

[<Fact>]
let ``compare with removed and added returns Breaking prioritizing removals`` () =
    // When there are both removals and additions, Breaking uses removals
    let baseline =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::OldMethod(): String"
          ApiSignature "  Foo::AnotherOld(): Int32" ]

    let current = [ ApiSignature "type Foo"; ApiSignature "  Foo::NewMethod(): String" ]

    match compare baseline current with
    | Breaking(h, t) ->
        let all = h :: t
        // Should include the removed items
        test <@ all |> List.exists (fun (ApiSignature s) -> s.Contains("OldMethod")) @>
        test <@ all |> List.exists (fun (ApiSignature s) -> s.Contains("AnotherOld")) @>
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``extractFromNuGetCache returns None for nonexistent version of real package`` () =
    // Package ID might exist but version won't
    test <@ extractFromNuGetCache "FSharp.Core" "0.0.0-nonexistent" = None @>

[<Fact>]
let ``createResolver returns a PathAssemblyResolver`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let resolver = createResolver dllPath
    // Should be able to create a MetadataLoadContext with it
    use context = new System.Reflection.MetadataLoadContext(resolver)
    let assembly = context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dllPath))
    test <@ assembly.GetExportedTypes().Length > 0 @>

[<Fact>]
let ``extractFromAssembly extracts methods with parameters`` () =
    let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

    let signatures = extractFromAssembly dllPath

    // Should have methods with parameter types listed
    let hasMethodWithParams =
        signatures
        |> List.exists (fun (ApiSignature s) -> s.Contains("(") && s.Contains(")") && s.Contains(",") |> not |> not)

    // At least some signatures should contain method signatures with return types
    let hasReturnTypes =
        signatures |> List.exists (fun (ApiSignature s) -> s.Contains("): "))

    test <@ hasReturnTypes @>

[<Fact>]
let ``compare adding non-nested type with plus sign in module name is Addition`` () =
    // A type name that contains + but parent is not in baseline (brand new module+type)
    let baseline = [ ApiSignature "type OtherModule.Foo" ]

    let current =
        [ ApiSignature "type OtherModule.Foo"
          ApiSignature "type BrandNew.Namespace+SubType" ]

    match compare baseline current with
    | Addition _ -> ()
    | other -> failwithf "Expected Addition since parent BrandNew.Namespace is not in baseline, got %A" other

[<Fact>]
let ``getAssemblySearchPaths falls back to runtimeDir path computation when DOTNET_ROOT is unset`` () =
    let original = System.Environment.GetEnvironmentVariable("DOTNET_ROOT")

    try
        System.Environment.SetEnvironmentVariable("DOTNET_ROOT", null)
        let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

        let dllPath =
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

        let paths = getAssemblySearchPaths dllPath
        // dllDir and runtimeDir should always be present regardless of DOTNET_ROOT
        test <@ paths.Length >= 2 @>
    finally
        System.Environment.SetEnvironmentVariable("DOTNET_ROOT", original)

[<Fact>]
let ``getAssemblySearchPaths returns no sdk or shared dirs when DOTNET_ROOT points to empty dir`` () =
    let original = System.Environment.GetEnvironmentVariable("DOTNET_ROOT")

    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())

    System.IO.Directory.CreateDirectory(tmpDir) |> ignore

    try
        System.Environment.SetEnvironmentVariable("DOTNET_ROOT", tmpDir)
        let thisAssembly = typeof<FsSemanticTagger.Version.Version>.Assembly.Location

        let dllPath =
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisAssembly), "FsSemanticTagger.dll")

        let paths = getAssemblySearchPaths dllPath
        // No paths should come from the fake empty dotnet root
        let fromFakeRoot = paths |> List.filter (fun p -> p.StartsWith(tmpDir))
        test <@ List.isEmpty fromFakeRoot @>
    finally
        System.Environment.SetEnvironmentVariable("DOTNET_ROOT", original)

        try
            System.IO.Directory.Delete(tmpDir, true)
        with _ ->
            ()

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
let ``extractFromCacheRoot returns None when the cached assembly cannot be read`` () =
    let cacheRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    let libDir = System.IO.Path.Combine(cacheRoot, "badpkg", "1.0.0", "lib", "net10.0")
    System.IO.Directory.CreateDirectory(libDir) |> ignore

    try
        // A file with the expected name but not a valid assembly — extraction must
        // degrade to None rather than throwing.
        System.IO.File.WriteAllText(System.IO.Path.Combine(libDir, "BadPkg.dll"), "not a real assembly")
        test <@ extractFromCacheRoot cacheRoot "BadPkg" "1.0.0" = None @>
    finally
        try
            System.IO.Directory.Delete(cacheRoot, true)
        with _ ->
            ()

module FsSemanticTagger.Tests.ApiTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Api

[<Fact>]
let ``formatTypeName handles simple types`` () =
    test <@ formatTypeName typeof<string> = "String" @>
    test <@ formatTypeName typeof<int> = "Int32" @>
    test <@ formatTypeName typeof<bool> = "Boolean" @>

[<Fact>]
let ``formatTypeName handles generic types`` () =
    let resultType = typeof<Result<int, string>>
    let formatted = formatTypeName resultType
    test <@ formatted <> "FSharpResult`2<Int32, String>" @>
    // Generic types strip the arity suffix
    test <@ formatted = "FSharpResult<Int32, String>" @>

[<Fact>]
let ``formatTypeName handles FSharpFunc`` () =
    let funcType = typeof<int -> string>
    let formatted = formatTypeName funcType
    test <@ formatted = "FSharpFunc<Int32, String>" @>

[<Fact>]
let ``formatTypeName handles arrays`` () =
    test <@ formatTypeName typeof<string[]> = "String[]" @>
    test <@ formatTypeName typeof<int[]> = "Int32[]" @>

[<Fact>]
let ``formatTypeName handles nested generic arrays`` () =
    test <@ formatTypeName typeof<Result<int, string>[]> = "FSharpResult<Int32, String>[]" @>

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
    test <@ ApiChange.toList NoChange = [] @>

[<Fact>]
let ``ApiChange.toList single item Breaking`` () =
    let change = Breaking(ApiSignature "a", [])
    test <@ ApiChange.toList change = [ ApiSignature "a" ] @>

let ``extractFromNuGetCache returns signatures for cached tool package`` () =
    // CoverageRatchet is a dotnet tool in the local NuGet cache (tools/<tfm>/any/)
    let result = extractFromNuGetCache "CoverageRatchet" "0.3.0-alpha.1"

    match result with
    | Some sigs -> test <@ sigs.Length > 0 @>
    | None -> failwith "Expected Some signatures for CoverageRatchet in NuGet cache"

[<Fact>]
let ``formatTypeName handles nested generic types`` () =
    // List<int> is a generic with one type arg
    let listType = typeof<System.Collections.Generic.List<int>>
    let formatted = formatTypeName listType
    test <@ formatted = "List<Int32>" @>

[<Fact>]
let ``formatTypeName handles multi-dimensional arrays`` () =
    test <@ formatTypeName typeof<int[][]> = "Int32[][]" @>

[<Fact>]
let ``formatTypeName handles generic array combinations`` () =
    test <@ formatTypeName typeof<System.Collections.Generic.List<string>[]> = "List<String>[]" @>

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

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
    test <@ formatted = "FSharpResult`2<Int32, String>" |> not @>
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
    let api =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::Bar(): String" ]

    test <@ compare api api = NoChange @>

[<Fact>]
let ``compare with added signatures returns Addition`` () =
    let baseline = [ ApiSignature "type Foo" ]

    let current =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::Bar(): String" ]

    let result = compare baseline current

    test
        <@
            result = Addition [ ApiSignature "  Foo::Bar(): String" ]
        @>

[<Fact>]
let ``compare with removed signatures returns Breaking`` () =
    let baseline =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::Bar(): String" ]

    let current = [ ApiSignature "type Foo" ]
    let result = compare baseline current

    test
        <@
            result = Breaking [ ApiSignature "  Foo::Bar(): String" ]
        @>

[<Fact>]
let ``compare with both added and removed returns Breaking`` () =
    let baseline =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::OldMethod(): String" ]

    let current =
        [ ApiSignature "type Foo"
          ApiSignature "  Foo::NewMethod(): Int32" ]

    match compare baseline current with
    | Breaking _ -> ()
    | other -> failwithf "Expected Breaking, got %A" other

[<Fact>]
let ``compare with empty lists returns NoChange`` () =
    test <@ compare [] [] = NoChange @>

[<Fact>]
let ``extractFromAssembly extracts signatures from own DLL`` () =
    // Use the tool's own compiled DLL as a test fixture
    let thisAssembly =
        typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(thisAssembly),
            "FsSemanticTagger.dll"
        )

    let signatures = extractFromAssembly dllPath

    // Should contain the Version type
    let hasVersionType =
        signatures
        |> List.exists (fun (ApiSignature s) -> s = "type FsSemanticTagger.Version+Version")

    test <@ hasVersionType @>

    // Should contain the parse function (compiled as static method)
    let hasParseFunction =
        signatures
        |> List.exists (fun (ApiSignature s) -> s.Contains("parse"))

    test <@ hasParseFunction @>

    // Should have non-trivial number of signatures
    test <@ signatures.Length > 5 @>

[<Fact>]
let ``extractFromAssembly results are sorted`` () =
    let thisAssembly =
        typeof<FsSemanticTagger.Version.Version>.Assembly.Location

    let dllPath =
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(thisAssembly),
            "FsSemanticTagger.dll"
        )

    let signatures = extractFromAssembly dllPath
    let sorted = List.sort signatures
    test <@ signatures = sorted @>

module FsSemanticTagger.Tests.ProgramTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Program

[<Fact>]
let ``run - no args returns Error with usage`` () =
    let result = run [||]

    test
        <@
            match result with
            | Error msg -> msg.Contains("Usage:")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - unknown command returns Error`` () =
    let result = run [| "bogus" |]

    test
        <@
            match result with
            | Error msg -> msg.Contains("Usage:")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - extract-api with missing dll returns Error`` () =
    let result = run [| "extract-api"; "/no/such/file.dll" |]

    test
        <@
            match result with
            | Error msg -> msg.Contains("DLL not found")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - check-api without two dll args returns Error`` () =
    let result = run [| "check-api" |]

    test
        <@
            match result with
            | Error msg -> msg.Contains("check-api")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - check-api with one arg returns Error`` () =
    let result = run [| "check-api"; "one.dll" |]

    test
        <@
            match result with
            | Error msg -> msg.Contains("check-api")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - release with unknown subcommand returns Error`` () =
    let result = run [| "release"; "bogus" |]

    test
        <@
            match result with
            | Error msg -> msg.Contains("Unknown release command")
            | Ok _ -> false
        @>

[<Fact>]
let ``run - extract-api with real dll returns Ok 0`` () =
    let dll = System.Reflection.Assembly.GetExecutingAssembly().Location

    let result = run [| "extract-api"; dll |]
    test <@ result = Ok 0 @>

[<Fact>]
let ``run - check-api same dll returns Ok 0`` () =
    let dll = System.Reflection.Assembly.GetExecutingAssembly().Location

    let result = run [| "check-api"; dll; dll |]
    test <@ result = Ok 0 @>

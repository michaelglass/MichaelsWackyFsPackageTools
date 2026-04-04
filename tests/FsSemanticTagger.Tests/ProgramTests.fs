module FsSemanticTagger.Tests.ProgramTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Program
open FsSemanticTagger

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

[<Fact>]
let ``init creates semantic-tagger.json for multiple packages`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())

    let srcDir1 = Path.Combine(tmpDir, "src", "ToolA")
    let srcDir2 = Path.Combine(tmpDir, "src", "ToolB")
    Directory.CreateDirectory(srcDir1) |> ignore
    Directory.CreateDirectory(srcDir2) |> ignore

    let packableFsproj name =
        sprintf
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>%s</PackageId>
  </PropertyGroup>
</Project>"""
            name

    File.WriteAllText(Path.Combine(srcDir1, "ToolA.fsproj"), packableFsproj "ToolA")
    File.WriteAllText(Path.Combine(srcDir2, "ToolB.fsproj"), packableFsproj "ToolB")

    try
        let result = initCommand tmpDir
        test <@ result = Ok 0 @>
        let jsonPath = Path.Combine(tmpDir, "semantic-tagger.json")
        test <@ File.Exists jsonPath @>
        let config = Config.parseJson (File.ReadAllText jsonPath)
        test <@ config.Packages.Length = 2 @>

        test
            <@ config.Packages |> List.exists (fun p -> p.Name = "ToolA" && p.TagPrefix = "toola-v") @>

        test
            <@ config.Packages |> List.exists (fun p -> p.Name = "ToolB" && p.TagPrefix = "toolb-v") @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``init uses v prefix for single package`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())

    let srcDir = Path.Combine(tmpDir, "src", "MyLib")
    Directory.CreateDirectory(srcDir) |> ignore

    File.WriteAllText(
        Path.Combine(srcDir, "MyLib.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""
    )

    try
        let result = initCommand tmpDir
        test <@ result = Ok 0 @>

        let config =
            Config.parseJson (File.ReadAllText(Path.Combine(tmpDir, "semantic-tagger.json")))

        test <@ config.Packages[0].TagPrefix = "v" @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``init does not overwrite existing config`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())

    Directory.CreateDirectory(tmpDir) |> ignore
    let jsonPath = Path.Combine(tmpDir, "semantic-tagger.json")
    File.WriteAllText(jsonPath, """{"packages":[]}""")

    try
        let result = initCommand tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(jsonPath) = """{"packages":[]}""" @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``init fails when no packable projects found`` () =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let result = initCommand tmpDir

        test
            <@ result = Error "No packable .fsproj files found. Each package needs a <PackageId> element." @>
    finally
        Directory.Delete(tmpDir, true)

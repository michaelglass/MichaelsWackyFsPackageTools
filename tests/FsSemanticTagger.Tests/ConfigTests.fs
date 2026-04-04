module FsSemanticTagger.Tests.ConfigTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Config
open Tests.Common.TestHelpers

let packableFsproj name =
    sprintf
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>%s</PackageId>
  </PropertyGroup>
</Project>"""
        name

[<Fact>]
let ``parseJson with single package`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "tagPrefix": "v"
                }
            ]
        }
        """

    let config = parseJson json
    test <@ config.Packages.Length = 1 @>
    test <@ config.Packages[0].Name = "MyLib" @>
    test <@ config.Packages[0].Fsproj = "src/MyLib/MyLib.fsproj" @>
    test <@ config.Packages[0].TagPrefix = "v" @>

    test <@ config.Packages[0].DllPath = Path.Combine("src/MyLib", "bin", "Release", "net10.0", "MyLib.dll") @>

    test <@ config.Packages[0].FsProjsSharingSameTag |> List.isEmpty @>
    test <@ config.ReservedVersions = Set.empty @>

[<Fact>]
let ``parseJson with multi-package includes fsProjsSharingSameTag`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "Core",
                    "fsproj": "src/Core/Core.fsproj",
                    "tagPrefix": "core-v",
                    "fsProjsSharingSameTag": ["src/Shared/Shared.fsproj"]
                },
                {
                    "name": "Plugin",
                    "fsproj": "src/Plugin/Plugin.fsproj",
                    "tagPrefix": "plugin-v"
                }
            ]
        }
        """

    let config = parseJson json
    test <@ config.Packages.Length = 2 @>
    test <@ config.Packages[0].Name = "Core" @>
    test <@ config.Packages[0].TagPrefix = "core-v" @>
    test <@ config.Packages[0].FsProjsSharingSameTag = [ "src/Shared/Shared.fsproj" ] @>
    test <@ config.Packages[1].Name = "Plugin" @>
    test <@ config.Packages[1].TagPrefix = "plugin-v" @>
    test <@ config.Packages[1].FsProjsSharingSameTag |> List.isEmpty @>

[<Fact>]
let ``parseJson with reservedVersions`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj"
                }
            ],
            "reservedVersions": ["0.1.0-alpha.1", "0.1.0-alpha.2"]
        }
        """

    let config = parseJson json
    test <@ config.ReservedVersions = set [ "0.1.0-alpha.1"; "0.1.0-alpha.2" ] @>

[<Fact>]
let ``discover with one packable fsproj`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
    <AssemblyName>MyLib</AssemblyName>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(Path.Combine(srcDir, "MyLib.fsproj"), fsprojContent)

        let config = discover tmpDir
        test <@ config.Packages.Length = 1 @>
        test <@ config.Packages[0].Name = "MyLib" @>
        test <@ config.Packages[0].TagPrefix = "v" @>

        test <@ config.Packages[0].DllPath = Path.Combine("src", "MyLib", "bin", "Release", "net10.0", "MyLib.dll") @>)

[<Fact>]
let ``discover skips non-packable fsproj`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        let testDir = Path.Combine(tmpDir, "tests", "MyLib.Tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testDir) |> ignore

        let packableFsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        let nonPackableFsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib.Tests</PackageId>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(Path.Combine(srcDir, "MyLib.fsproj"), packableFsproj)
        File.WriteAllText(Path.Combine(testDir, "MyLib.Tests.fsproj"), nonPackableFsproj)

        let config = discover tmpDir
        test <@ config.Packages.Length = 1 @>
        test <@ config.Packages[0].Name = "MyLib" @>)

[<Fact>]
let ``load prefers JSON file over discovery`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(Path.Combine(srcDir, "MyLib.fsproj"), fsprojContent)

        let jsonContent =
            """
        {
            "packages": [
                {
                    "name": "CustomName",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "tagPrefix": "custom-v"
                }
            ]
        }
        """

        File.WriteAllText(Path.Combine(tmpDir, "semantic-tagger.json"), jsonContent)

        let config = load tmpDir
        test <@ config.Packages[0].Name = "CustomName" @>
        test <@ config.Packages[0].TagPrefix = "custom-v" @>)

[<Fact>]
let ``discover fails with no packable fsproj`` () =
    withTempDir (fun tmpDir ->
        let nonPackableFsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""

        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Lib.fsproj"), nonPackableFsproj)

        let ex = Assert.Throws<System.Exception>(fun () -> discover tmpDir |> ignore)
        test <@ ex.Message.Contains("No packable") @>)

[<Fact>]
let ``discover fails with multiple packable fsprojs`` () =
    withTempDir (fun tmpDir ->
        let srcDir1 = Path.Combine(tmpDir, "src", "Lib1")
        let srcDir2 = Path.Combine(tmpDir, "src", "Lib2")
        Directory.CreateDirectory(srcDir1) |> ignore
        Directory.CreateDirectory(srcDir2) |> ignore

        File.WriteAllText(Path.Combine(srcDir1, "Lib1.fsproj"), packableFsproj "Lib1")
        File.WriteAllText(Path.Combine(srcDir2, "Lib2.fsproj"), packableFsproj "Lib2")

        let ex = Assert.Throws<System.Exception>(fun () -> discover tmpDir |> ignore)
        test <@ ex.Message.Contains("2 packable") @>)

[<Fact>]
let ``parseJson with explicit dllPath`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "dllPath": "custom/path/MyLib.dll"
                }
            ]
        }
        """

    let config = parseJson json
    test <@ config.Packages[0].DllPath = "custom/path/MyLib.dll" @>

[<Fact>]
let ``parseJson uses default tagPrefix when not specified`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj"
                }
            ]
        }
        """

    let config = parseJson json
    test <@ config.Packages[0].TagPrefix = "v" @>

[<Fact>]
let ``load falls back to discover when no JSON file`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(Path.Combine(srcDir, "MyLib.fsproj"), fsprojContent)

        let config = load tmpDir
        test <@ config.Packages[0].Name = "MyLib" @>
        test <@ config.Packages[0].TagPrefix = "v" @>)

[<Fact>]
let ``findPackableProjects finds packable fsproj files`` () =
    withTempDir (fun tmpDir ->
        let srcDir1 = Path.Combine(tmpDir, "src", "Lib1")
        let srcDir2 = Path.Combine(tmpDir, "src", "Lib2")
        Directory.CreateDirectory(srcDir1) |> ignore
        Directory.CreateDirectory(srcDir2) |> ignore

        File.WriteAllText(Path.Combine(srcDir1, "Lib1.fsproj"), packableFsproj "Lib1")
        File.WriteAllText(Path.Combine(srcDir2, "Lib2.fsproj"), packableFsproj "Lib2")

        let projects = findPackableProjects tmpDir
        test <@ projects.Length = 2 @>
        test <@ projects |> List.exists (fun (name, _) -> name = "Lib1") @>
        test <@ projects |> List.exists (fun (name, _) -> name = "Lib2") @>)

[<Fact>]
let ``findPackableProjects skips non-packable`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        let testDir = Path.Combine(tmpDir, "tests", "MyLib.Tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testDir) |> ignore

        File.WriteAllText(
            Path.Combine(srcDir, "MyLib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""
        )

        File.WriteAllText(
            Path.Combine(testDir, "MyLib.Tests.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib.Tests</PackageId>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>"""
        )

        let projects = findPackableProjects tmpDir
        test <@ projects.Length = 1 @>
        test <@ projects[0] |> fst = "MyLib" @>)

[<Fact>]
let ``toJson roundtrips through parseJson`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "mylib-v"
                FsProjsSharingSameTag = [ "src/Shared/Shared.fsproj" ] } ]
          ReservedVersions = set [ "1.0.0" ] }

    let json = toJson config
    let roundtripped = parseJson json
    test <@ roundtripped.Packages.Length = 1 @>
    test <@ roundtripped.Packages[0].Name = "MyLib" @>
    test <@ roundtripped.Packages[0].Fsproj = "src/MyLib/MyLib.fsproj" @>
    test <@ roundtripped.Packages[0].TagPrefix = "mylib-v" @>
    test <@ roundtripped.Packages[0].FsProjsSharingSameTag = [ "src/Shared/Shared.fsproj" ] @>
    test <@ roundtripped.ReservedVersions = set [ "1.0.0" ] @>

[<Fact>]
let ``toJson omits empty reservedVersions`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty }

    let json = toJson config
    test <@ not (json.Contains "reservedVersions") @>

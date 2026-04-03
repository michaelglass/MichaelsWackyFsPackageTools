module FsSemanticTagger.Tests.ConfigTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Config

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

    test
        <@
            config.Packages[0].DllPath = Path.Combine("src/MyLib", "bin", "Release", "net10.0", "MyLib.dll")
        @>

    test <@ config.Packages[0].ExtraFsprojs = [] @>
    test <@ config.ReservedVersions = Set.empty @>

[<Fact>]
let ``parseJson with multi-package includes extraFsprojs`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "Core",
                    "fsproj": "src/Core/Core.fsproj",
                    "tagPrefix": "core-v",
                    "extraFsprojs": ["src/Shared/Shared.fsproj"]
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
    test <@ config.Packages[0].ExtraFsprojs = [ "src/Shared/Shared.fsproj" ] @>
    test <@ config.Packages[1].Name = "Plugin" @>
    test <@ config.Packages[1].TagPrefix = "plugin-v" @>
    test <@ config.Packages[1].ExtraFsprojs = [] @>

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
    let tmpDir = Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())
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

    try
        let config = discover tmpDir
        test <@ config.Packages.Length = 1 @>
        test <@ config.Packages[0].Name = "MyLib" @>
        test <@ config.Packages[0].TagPrefix = "v" @>

        test
            <@
                config.Packages[0].DllPath = Path.Combine(
                    "src",
                    "MyLib",
                    "bin",
                    "Release",
                    "net10.0",
                    "MyLib.dll"
                )
            @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``discover skips non-packable fsproj`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())
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

    try
        let config = discover tmpDir
        test <@ config.Packages.Length = 1 @>
        test <@ config.Packages[0].Name = "MyLib" @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``load prefers JSON file over discovery`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fssemtagger-test-" + Path.GetRandomFileName())
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

    try
        let config = load tmpDir
        test <@ config.Packages[0].Name = "CustomName" @>
        test <@ config.Packages[0].TagPrefix = "custom-v" @>
    finally
        Directory.Delete(tmpDir, true)

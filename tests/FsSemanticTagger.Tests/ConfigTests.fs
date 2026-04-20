module FsSemanticTagger.Tests.ConfigTests

open System.IO
open Xunit
open Tests.Common
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
let ``parseJson with preBuildCmds`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj"
                }
            ],
            "preBuildCmds": ["dotnet tool restore", "dotnet tool run paket restore"]
        }
        """

    let config = parseJson json
    test <@ config.PreBuildCmds = [ "dotnet tool restore"; "dotnet tool run paket restore" ] @>

[<Fact>]
let ``parseJson defaults preBuildCmds to empty`` () =
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
    test <@ List.isEmpty config.PreBuildCmds @>

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

        let config = discover tmpDir |> Result.defaultWith failwith
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

        let config = discover tmpDir |> Result.defaultWith failwith
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

        let config = load tmpDir |> Result.defaultWith failwith
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

        let result = discover tmpDir

        test
            <@
                match result with
                | Error msg -> msg.Contains("No packable")
                | Ok _ -> false
            @>)

[<Fact>]
let ``discover fails with multiple packable fsprojs`` () =
    withTempDir (fun tmpDir ->
        let srcDir1 = Path.Combine(tmpDir, "src", "Lib1")
        let srcDir2 = Path.Combine(tmpDir, "src", "Lib2")
        Directory.CreateDirectory(srcDir1) |> ignore
        Directory.CreateDirectory(srcDir2) |> ignore

        File.WriteAllText(Path.Combine(srcDir1, "Lib1.fsproj"), packableFsproj "Lib1")
        File.WriteAllText(Path.Combine(srcDir2, "Lib2.fsproj"), packableFsproj "Lib2")

        let result = discover tmpDir

        test
            <@
                match result with
                | Error msg -> msg.Contains("2 packable")
                | Ok _ -> false
            @>)

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

        let config = load tmpDir |> Result.defaultWith failwith
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
          ReservedVersions = set [ "1.0.0" ]
          PreBuildCmds = [] }

    let json = toJson config
    let roundtripped = parseJson json
    test <@ roundtripped.Packages.Length = 1 @>
    test <@ roundtripped.Packages[0].Name = "MyLib" @>
    test <@ roundtripped.Packages[0].Fsproj = "src/MyLib/MyLib.fsproj" @>
    test <@ roundtripped.Packages[0].TagPrefix = "mylib-v" @>
    test <@ roundtripped.Packages[0].FsProjsSharingSameTag = [ "src/Shared/Shared.fsproj" ] @>
    test <@ roundtripped.ReservedVersions = set [ "1.0.0" ] @>

[<Fact>]
let ``load keeps dllPath from JSON when fsproj missing on disk`` () =
    withTempDir (fun tmpDir ->
        let jsonContent =
            """
        {
            "packages": [
                {
                    "name": "Ghost",
                    "fsproj": "src/Ghost/Ghost.fsproj",
                    "dllPath": "custom/Ghost.dll"
                }
            ]
        }
        """

        File.WriteAllText(Path.Combine(tmpDir, "semantic-tagger.json"), jsonContent)

        let config = load tmpDir |> Result.defaultWith failwith
        test <@ config.Packages[0].DllPath = "custom/Ghost.dll" @>)

[<Fact>]
let ``toJson includes preBuildCmds when not empty`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [ "dotnet tool restore" ] }

    let json = toJson config
    test <@ json.Contains "preBuildCmds" @>
    test <@ json.Contains "dotnet tool restore" @>

[<Fact>]
let ``toJson omits empty preBuildCmds`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [] }

    let json = toJson config
    test <@ not (json.Contains "preBuildCmds") @>

[<Fact>]
let ``toJson omits empty fsProjsSharingSameTag`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [] }

    let json = toJson config
    test <@ not (json.Contains "fsProjsSharingSameTag") @>

[<Fact>]
let ``toJson includes reservedVersions when not empty`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = set [ "1.0.0"; "2.0.0" ]
          PreBuildCmds = [] }

    let json = toJson config
    test <@ json.Contains "reservedVersions" @>
    test <@ json.Contains "1.0.0" @>
    test <@ json.Contains "2.0.0" @>

[<Fact>]
let ``parseJson with all optional fields present`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "tagPrefix": "mylib-v",
                    "dllPath": "custom/MyLib.dll",
                    "fsProjsSharingSameTag": ["src/Shared/Shared.fsproj"]
                }
            ],
            "reservedVersions": ["1.0.0"],
            "preBuildCmds": ["dotnet restore"]
        }
        """

    let config = parseJson json
    test <@ config.Packages[0].TagPrefix = "mylib-v" @>
    test <@ config.Packages[0].DllPath = "custom/MyLib.dll" @>
    test <@ config.Packages[0].FsProjsSharingSameTag = [ "src/Shared/Shared.fsproj" ] @>
    test <@ config.ReservedVersions = set [ "1.0.0" ] @>
    test <@ config.PreBuildCmds = [ "dotnet restore" ] @>

[<Fact>]
let ``parseJson with all optional fields absent`` () =
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
    test <@ List.isEmpty config.Packages[0].FsProjsSharingSameTag @>
    test <@ config.ReservedVersions = Set.empty @>
    test <@ List.isEmpty config.PreBuildCmds @>

[<Fact>]
let ``load returns Error when discover fails with no packable`` () =
    withTempDir (fun tmpDir ->
        let result = load tmpDir

        test
            <@
                match result with
                | Error msg -> msg.Contains("No packable")
                | Ok _ -> false
            @>)

[<Fact>]
let ``discover returns Error with multiple packable fsprojs`` () =
    withTempDir (fun tmpDir ->
        let srcDir1 = Path.Combine(tmpDir, "src", "Lib1")
        let srcDir2 = Path.Combine(tmpDir, "src", "Lib2")
        let srcDir3 = Path.Combine(tmpDir, "src", "Lib3")
        Directory.CreateDirectory(srcDir1) |> ignore
        Directory.CreateDirectory(srcDir2) |> ignore
        Directory.CreateDirectory(srcDir3) |> ignore

        File.WriteAllText(Path.Combine(srcDir1, "Lib1.fsproj"), packableFsproj "Lib1")
        File.WriteAllText(Path.Combine(srcDir2, "Lib2.fsproj"), packableFsproj "Lib2")
        File.WriteAllText(Path.Combine(srcDir3, "Lib3.fsproj"), packableFsproj "Lib3")

        let result = discover tmpDir

        test
            <@
                match result with
                | Error msg -> msg.Contains("3 packable")
                | Ok _ -> false
            @>)

[<Fact>]
let ``toJson omits empty reservedVersions`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [] }

    let json = toJson config
    test <@ not (json.Contains "reservedVersions") @>

[<Fact>]
let ``deriveDllPathFromContent with AssemblyName override`` () =
    let fsprojPath = "/some/path/MyLib.fsproj"

    let content =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>CustomAssemblyName</AssemblyName>
  </PropertyGroup>
</Project>"""

    let result = deriveDllPathFromContent fsprojPath content
    test <@ result.Contains("CustomAssemblyName.dll") @>
    test <@ not (result.Contains("MyLib.dll")) @>

[<Fact>]
let ``deriveDllPathFromContent without AssemblyName uses fsproj name`` () =
    let fsprojPath = "/some/path/MyLib.fsproj"

    let content =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

    let result = deriveDllPathFromContent fsprojPath content
    test <@ result.Contains("MyLib.dll") @>

[<Fact>]
let ``load re-derives dllPath from fsproj with AssemblyName`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>MyLib</PackageId>
    <AssemblyName>MyCustomAssembly</AssemblyName>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(Path.Combine(srcDir, "MyLib.fsproj"), fsprojContent)

        let jsonContent =
            """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "dllPath": "old/path/MyLib.dll"
                }
            ]
        }
        """

        File.WriteAllText(Path.Combine(tmpDir, "semantic-tagger.json"), jsonContent)

        let config = load tmpDir |> Result.defaultWith failwith
        // Should re-derive from fsproj, using AssemblyName
        test <@ config.Packages[0].DllPath.Contains("MyCustomAssembly.dll") @>
        // Should NOT keep the old dllPath from JSON
        test <@ not (config.Packages[0].DllPath.Contains("old/path")) @>)

[<Fact>]
let ``parseJson with empty reservedVersions array`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj"
                }
            ],
            "reservedVersions": []
        }
        """

    let config = parseJson json
    test <@ config.ReservedVersions = Set.empty @>

[<Fact>]
let ``parseJson with empty preBuildCmds array`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj"
                }
            ],
            "preBuildCmds": []
        }
        """

    let config = parseJson json
    test <@ List.isEmpty config.PreBuildCmds @>

[<Fact>]
let ``parseJson with empty fsProjsSharingSameTag array`` () =
    let json =
        """
        {
            "packages": [
                {
                    "name": "MyLib",
                    "fsproj": "src/MyLib/MyLib.fsproj",
                    "fsProjsSharingSameTag": []
                }
            ]
        }
        """

    let config = parseJson json
    test <@ List.isEmpty config.Packages[0].FsProjsSharingSameTag @>

[<Fact>]
let ``toJson roundtrips with preBuildCmds`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [ "dotnet tool restore"; "dotnet build" ] }

    let json = toJson config
    let roundtripped = parseJson json
    test <@ roundtripped.PreBuildCmds = [ "dotnet tool restore"; "dotnet build" ] @>

[<Fact>]
let ``toJson roundtrips with empty collections`` () =
    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [] }

    let json = toJson config
    let roundtripped = parseJson json
    test <@ roundtripped.Packages.Length = 1 @>
    test <@ roundtripped.ReservedVersions = Set.empty @>
    test <@ List.isEmpty roundtripped.PreBuildCmds @>
    test <@ List.isEmpty roundtripped.Packages[0].FsProjsSharingSameTag @>

[<Fact>]
let ``findPackableProjects ignores fsproj without PackageId`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "Internal")
        Directory.CreateDirectory(srcDir) |> ignore

        File.WriteAllText(
            Path.Combine(srcDir, "Internal.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""
        )

        let projects = findPackableProjects tmpDir
        test <@ projects.Length = 0 @>)

[<Fact>]
let ``deriveDllPathFromContent uses correct output path structure`` () =
    let fsprojPath = "/repo/src/MyLib/MyLib.fsproj"

    let content =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

    let result = deriveDllPathFromContent fsprojPath content
    test <@ result = Path.Combine("/repo/src/MyLib", "bin", "Release", "net10.0", "MyLib.dll") @>

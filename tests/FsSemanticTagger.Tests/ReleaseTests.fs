module FsSemanticTagger.Tests.ReleaseTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.Release

[<Fact>]
let ``updateFsprojVersion - updates Version element in fsproj`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            { Major = 2
              Minor = 3
              Patch = 4
              Stage = Stable }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>2.3.4</Version>") @>
        test <@ not (result.Contains("<Version>1.0.0</Version>")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``updateFsprojVersion - handles pre-release versions`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            { Major = 0
              Minor = 2
              Patch = 0
              Stage = PreRelease(Alpha 1) }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - returns 1 when uncommitted changes`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "Working copy changes:\nM src/Foo.fs"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty }

    let result = release fakeRun config Auto GitHubActions
    test <@ result = 1 @>

[<Fact>]
let ``release - Auto with no previous tags returns 0 with no packages`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "git", arg when arg.StartsWith("tag -l") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty }

    let result = release fakeRun config Auto GitHubActions
    test <@ result = 0 @>

[<Fact>]
let ``release - StartAlpha with FirstRelease creates version`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", arg when arg.StartsWith("commit -m") -> Success ""
            | "jj", arg when arg.StartsWith("tag set") -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty }

        let result = release fakeRun config StartAlpha GitHubActions
        test <@ result = 0 @>

        // Verify the fsproj was updated with firstAlpha version
        let updatedContent = File.ReadAllText(tmpFile)
        test <@ updatedContent.Contains("<Version>0.1.0-alpha.1</Version>") @>

        // Verify commit and tag were called
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("commit")) @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set v0.1.0-alpha.1"))
            @>
    finally
        File.Delete(tmpFile)

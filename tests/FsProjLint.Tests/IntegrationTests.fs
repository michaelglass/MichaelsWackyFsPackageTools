module FsProjLint.Tests.IntegrationTests

open System.IO
open Xunit
open Swensen.Unquote
open FsProjLint.Checks

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore

    try
        f dir
    finally
        Directory.Delete(dir, true)

let private createFile (dir: string) (relativePath: string) (content: string) =
    let fullPath = Path.Combine(dir, relativePath)
    let parent = Path.GetDirectoryName(fullPath)
    Directory.CreateDirectory(parent) |> ignore
    File.WriteAllText(fullPath, content)

let private validFsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <PackageId>MyPackage</PackageId>
    <Version>1.0.0</Version>
    <Description>A test package</Description>
    <Authors>testauthor</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/test/test</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
  </ItemGroup>
</Project>"""

[<Fact>]
let ``complete valid repo passes all checks`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE" ""
        createFile dir "README.md" ""
        createFile dir ".editorconfig" ""
        createFile dir "docs/index.md" ""
        createFile dir "src/MyProject/MyProject.fsproj" validFsproj

        let result = runLint dir
        let allChecks = result.RepoChecks @ (result.ProjectChecks |> List.collect snd)

        test <@ allChecks |> List.forall (fun c -> c.Passed) @>)

[<Fact>]
let ``repo with issues reports correct failures`` () =
    withTempDir (fun dir ->
        // Missing LICENSE, README, editorconfig, docs/index.md
        let fsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
  </PropertyGroup>
</Project>"""

        createFile dir "src/MyProject/MyProject.fsproj" fsproj

        let result = runLint dir
        let allChecks = result.RepoChecks @ (result.ProjectChecks |> List.collect snd)
        let failed = allChecks |> List.filter (fun c -> not c.Passed)

        // Should fail on: LICENSE, README, .editorconfig, docs/index.md, TreatWarningsAsErrors,
        // plus all packable checks (Version, Description, Authors, PackageLicenseExpression,
        // RepositoryUrl, RepositoryType, GenerateDocumentationFile, IncludeSymbols,
        // SymbolPackageFormat, SourceLink)
        test <@ failed.Length > 0 @>

        let failedNames = failed |> List.map (fun c -> c.Name)

        test <@ failedNames |> List.contains "LICENSE exists" @>
        test <@ failedNames |> List.contains "README.md exists" @>
        test <@ failedNames |> List.contains ".editorconfig exists" @>
        test <@ failedNames |> List.contains "TreatWarningsAsErrors is true" @>
        test <@ failedNames |> List.contains "Description present" @>)

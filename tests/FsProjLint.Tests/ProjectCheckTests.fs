module FsProjLint.Tests.ProjectCheckTests

open System.IO
open Xunit
open Swensen.Unquote
open FsProjLint.Checks

let private withTempFsproj (content: string) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(dir) |> ignore
    let fsprojPath = Path.Combine(dir, "Test.fsproj")
    File.WriteAllText(fsprojPath, content)

    try
        f fsprojPath
    finally
        Directory.Delete(dir, true)

let private allProjectFsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>"""

let private packableFsproj =
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

let private notPackableFsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <PackageId>MyPackage</PackageId>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>"""

let private noPackageIdFsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>"""

[<Fact>]
let ``all-project check passes with TreatWarningsAsErrors true`` () =
    withTempFsproj allProjectFsproj (fun path ->
        let results = checkProject path

        let twaCheck =
            results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

        test <@ twaCheck.Passed @>)

[<Fact>]
let ``fails when TreatWarningsAsErrors missing`` () =
    let fsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>"""

    withTempFsproj fsproj (fun path ->
        let results = checkProject path

        let twaCheck =
            results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

        test <@ not twaCheck.Passed @>)

[<Fact>]
let ``packable project passes with all required metadata`` () =
    withTempFsproj packableFsproj (fun path ->
        let results = checkProject path

        test <@ results |> List.forall (fun r -> r.Passed) @>)

[<Fact>]
let ``packable project fails when missing SourceLink`` () =
    let fsproj =
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
</Project>"""

    withTempFsproj fsproj (fun path ->
        let results = checkProject path

        let sourcelinkCheck =
            results |> List.find (fun r -> r.Name = "Has Microsoft.SourceLink.GitHub")

        test <@ not sourcelinkCheck.Passed @>)

[<Fact>]
let ``packable project fails when missing Description`` () =
    let fsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <PackageId>MyPackage</PackageId>
    <Version>1.0.0</Version>
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

    withTempFsproj fsproj (fun path ->
        let results = checkProject path

        let descCheck = results |> List.find (fun r -> r.Name = "Description present")

        test <@ not descCheck.Passed @>)

[<Fact>]
let ``non-packable project with IsPackable false skips package checks`` () =
    withTempFsproj notPackableFsproj (fun path ->
        let results = checkProject path

        test <@ results.Length = 1 @>

        test <@ results.[0].Name = "TreatWarningsAsErrors is true" && results.[0].Passed @>)

[<Fact>]
let ``non-packable project with no PackageId skips package checks`` () =
    withTempFsproj noPackageIdFsproj (fun path ->
        let results = checkProject path

        test <@ results.Length = 1 @>

        test <@ results.[0].Name = "TreatWarningsAsErrors is true" && results.[0].Passed @>)

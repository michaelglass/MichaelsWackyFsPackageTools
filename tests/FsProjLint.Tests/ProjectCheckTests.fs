module FsProjLint.Tests.ProjectCheckTests

open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsProjLint.Checks
open FsProjLint.Tests.TestFixtures

let private checkXml (content: string) =
    let doc = XDocument.Parse(content)
    checkProject "Test.fsproj" doc

let private allProjectFsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
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
    let results = checkXml allProjectFsproj

    let twaCheck =
        results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    test <@ twaCheck.Passed @>

[<Fact>]
let ``fails when TreatWarningsAsErrors missing`` () =
    let fsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>"""

    let results = checkXml fsproj

    let twaCheck =
        results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    test <@ not twaCheck.Passed @>

[<Fact>]
let ``packable project passes with all required metadata`` () =
    let results = checkXml packableFsproj

    test <@ results |> List.forall (fun r -> r.Passed) @>

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

    let results = checkXml fsproj

    let sourcelinkCheck =
        results |> List.find (fun r -> r.Name = "Has Microsoft.SourceLink.GitHub")

    test <@ not sourcelinkCheck.Passed @>

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

    let results = checkXml fsproj

    let descCheck = results |> List.find (fun r -> r.Name = "Description present")

    test <@ not descCheck.Passed @>

[<Fact>]
let ``non-packable project with IsPackable false skips package checks`` () =
    let results = checkXml notPackableFsproj

    test <@ results.Length = 1 @>

    test <@ results.[0].Name = "TreatWarningsAsErrors is true" && results.[0].Passed @>

[<Fact>]
let ``non-packable project with no PackageId skips package checks`` () =
    let results = checkXml noPackageIdFsproj

    test <@ results.Length = 1 @>

    test <@ results.[0].Name = "TreatWarningsAsErrors is true" && results.[0].Passed @>

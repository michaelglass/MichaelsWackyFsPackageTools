module FsProjLint.Tests.ProjectCheckTests

open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsProjLint.Checks
open FsProjLint.Tests.TestFixtures

let private checkXml (content: string) =
    let doc = XDocument.Parse(content)
    checkProject doc

let private isPassed (result: CheckResult) =
    match result.Outcome with
    | Passed -> true
    | Failed _ -> false

let private isFailed (result: CheckResult) =
    match result.Outcome with
    | Passed -> false
    | Failed _ -> true

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

// -- TreatWarningsAsErrors --

[<Fact>]
let ``all-project check passes with TreatWarningsAsErrors true`` () =
    let results = checkXml allProjectFsproj

    let twaCheck =
        results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    test <@ isPassed twaCheck @>

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

    test <@ isFailed twaCheck @>

[<Fact>]
let ``fails when TreatWarningsAsErrors is false`` () =
    let fsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>"""

    let results = checkXml fsproj

    let twaCheck =
        results |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    test <@ isFailed twaCheck @>

    test
        <@
            match twaCheck.Outcome with
            | Failed reason -> reason.Contains("'false'") && reason.Contains("'true'")
            | Passed -> false
        @>

// -- Packable project checks --

[<Fact>]
let ``packable project passes with all required metadata`` () =
    let results = checkXml packableFsproj

    test <@ results |> List.forall isPassed @>

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

    test <@ isFailed sourcelinkCheck @>

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

    test <@ isFailed descCheck @>

// -- isPackable --

[<Fact>]
let ``non-packable project with IsPackable false skips package checks`` () =
    let results = checkXml notPackableFsproj

    test <@ results.Length = 1 @>

    test <@ results.[0].Name = "TreatWarningsAsErrors is true" && isPassed results.[0] @>

[<Fact>]
let ``non-packable project with no PackageId skips package checks`` () =
    let results = checkXml noPackageIdFsproj

    test <@ results.Length = 1 @>

    test <@ results.[0].Name = "TreatWarningsAsErrors is true" && isPassed results.[0] @>

[<Fact>]
let ``isPackable returns true with PackageId and IsPackable true`` () =
    let doc =
        XDocument.Parse(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>"""
        )

    test <@ isPackable doc @>

[<Fact>]
let ``isPackable returns false with PackageId and IsPackable false`` () =
    let doc =
        XDocument.Parse(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
</Project>"""
        )

    test <@ not (isPackable doc) @>

[<Fact>]
let ``isPackable returns false with no PackageId`` () =
    let doc =
        XDocument.Parse(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>"""
        )

    test <@ not (isPackable doc) @>

[<Fact>]
let ``isPackable returns true with PackageId and no IsPackable property`` () =
    let doc =
        XDocument.Parse(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
  </PropertyGroup>
</Project>"""
        )

    test <@ isPackable doc @>

// -- getProperty --

[<Fact>]
let ``getProperty returns Some for present property`` () =
    let doc =
        XDocument.Parse(
            """<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>"""
        )

    test <@ getProperty doc "Version" = Some "1.0.0" @>

[<Fact>]
let ``getProperty returns None for missing property`` () =
    let doc =
        XDocument.Parse("""<Project><PropertyGroup></PropertyGroup></Project>""")

    test <@ getProperty doc "Version" = None @>

// -- hasPackageRef --

[<Fact>]
let ``hasPackageRef returns true for present reference`` () =
    let doc =
        XDocument.Parse(
            """<Project>
  <ItemGroup>
    <PackageReference Include="SomePackage" Version="1.0.0" />
  </ItemGroup>
</Project>"""
        )

    test <@ hasPackageRef doc "SomePackage" @>

[<Fact>]
let ``hasPackageRef returns false for missing reference`` () =
    let doc =
        XDocument.Parse(
            """<Project>
  <ItemGroup>
    <PackageReference Include="OtherPackage" Version="1.0.0" />
  </ItemGroup>
</Project>"""
        )

    test <@ not (hasPackageRef doc "SomePackage") @>

[<Fact>]
let ``hasPackageRef returns false when no PackageReference elements`` () =
    let doc =
        XDocument.Parse("""<Project><ItemGroup></ItemGroup></Project>""")

    test <@ not (hasPackageRef doc "SomePackage") @>

[<Fact>]
let ``hasPackageRef returns false when PackageReference has no Include attribute`` () =
    let doc =
        XDocument.Parse(
            """<Project>
  <ItemGroup>
    <PackageReference Update="SomePackage" Version="1.0.0" />
  </ItemGroup>
</Project>"""
        )

    test <@ not (hasPackageRef doc "SomePackage") @>

// -- Individual package checks (present and missing) --

let private makePackableWith (properties: string) (itemGroups: string) =
    sprintf
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <PackageId>MyPackage</PackageId>
    %s
  </PropertyGroup>
  %s
</Project>"""
        properties
        itemGroups

let private fullProperties =
    """<Version>1.0.0</Version>
    <Description>A test package</Description>
    <Authors>testauthor</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/test/test</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>"""

let private fullItemGroup =
    """<ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
  </ItemGroup>"""

let private removeProperty (propName: string) (props: string) =
    props
    |> fun s ->
        System.Text.RegularExpressions.Regex.Replace(
            s,
            sprintf """<\s*%s\s*>[^<]*</\s*%s\s*>""" propName propName,
            ""
        )

[<Theory>]
[<InlineData("Version", "Version present")>]
[<InlineData("Authors", "Authors present")>]
[<InlineData("PackageLicenseExpression", "PackageLicenseExpression present")>]
[<InlineData("RepositoryUrl", "RepositoryUrl present")>]
[<InlineData("RepositoryType", "RepositoryType present")>]
let ``packable project fails when missing present-check property`` (propName: string) (checkName: string) =
    let props = removeProperty propName fullProperties
    let fsproj = makePackableWith props fullItemGroup
    let results = checkXml fsproj
    let check = results |> List.find (fun r -> r.Name = checkName)

    test <@ isFailed check @>

[<Theory>]
[<InlineData("Version", "Version present")>]
[<InlineData("Authors", "Authors present")>]
[<InlineData("PackageLicenseExpression", "PackageLicenseExpression present")>]
[<InlineData("RepositoryUrl", "RepositoryUrl present")>]
[<InlineData("RepositoryType", "RepositoryType present")>]
let ``packable project passes when present-check property exists`` (propName: string) (checkName: string) =
    let _ = propName
    let fsproj = makePackableWith fullProperties fullItemGroup
    let results = checkXml fsproj
    let check = results |> List.find (fun r -> r.Name = checkName)

    test <@ isPassed check @>

[<Theory>]
[<InlineData("GenerateDocumentationFile", "GenerateDocumentationFile is true")>]
[<InlineData("IncludeSymbols", "IncludeSymbols is true")>]
[<InlineData("SymbolPackageFormat", "SymbolPackageFormat is snupkg")>]
let ``packable project fails when missing equals-check property`` (propName: string) (checkName: string) =
    let props = removeProperty propName fullProperties
    let fsproj = makePackableWith props fullItemGroup
    let results = checkXml fsproj
    let check = results |> List.find (fun r -> r.Name = checkName)

    test <@ isFailed check @>

[<Theory>]
[<InlineData("GenerateDocumentationFile", "GenerateDocumentationFile is true")>]
[<InlineData("IncludeSymbols", "IncludeSymbols is true")>]
[<InlineData("SymbolPackageFormat", "SymbolPackageFormat is snupkg")>]
let ``packable project passes when equals-check property correct`` (_propName: string) (checkName: string) =
    let fsproj = makePackableWith fullProperties fullItemGroup
    let results = checkXml fsproj
    let check = results |> List.find (fun r -> r.Name = checkName)

    test <@ isPassed check @>

// -- checkPropertyEquals edge cases --

[<Fact>]
let ``checkPropertyEquals reports wrong value differently from missing`` () =
    let wrongValueFsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>"""

    let missingFsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>"""

    let wrongResults = checkXml wrongValueFsproj
    let missingResults = checkXml missingFsproj

    let wrongCheck =
        wrongResults
        |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    let missingCheck =
        missingResults
        |> List.find (fun r -> r.Name = "TreatWarningsAsErrors is true")

    test
        <@
            match wrongCheck.Outcome with
            | Failed reason -> reason.Contains("'false'")
            | Passed -> false
        @>

    test
        <@
            match missingCheck.Outcome with
            | Failed reason -> reason.Contains("not found")
            | Passed -> false
        @>

// -- checkPropertyPresent with empty value --

[<Fact>]
let ``checkPropertyPresent fails with empty value`` () =
    let fsproj =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Version></Version>
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

    let results = checkXml fsproj
    let versionCheck = results |> List.find (fun r -> r.Name = "Version present")

    test <@ isFailed versionCheck @>

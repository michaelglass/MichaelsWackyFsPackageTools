module FsProjLint.Tests.TestFixtures

let packableFsproj =
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

/// A project parsed from `xml` with no Directory.Build.props, for checks that
/// read only the project's own properties.
let internal projectOf (xml: string) : Shared.MsBuildProject.Project =
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyPackage.fsproj")
        Document = System.Xml.Linq.XDocument.Parse xml
        DirectoryBuildProps = None
    }

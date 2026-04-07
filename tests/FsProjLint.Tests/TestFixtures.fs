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

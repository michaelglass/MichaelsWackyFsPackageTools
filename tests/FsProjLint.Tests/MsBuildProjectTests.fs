module FsProjLint.Tests.MsBuildProjectTests

// Shared.MsBuildProject: the one reader of project properties (the project,
// else the nearest Directory.Build.props) and the one packable rule, shared by
// FsProjLint and FsSemanticTagger.

open System.IO
open Xunit
open Swensen.Unquote
open Shared.MsBuildProject
open FsProjLint.Checks
open Tests.Common.TestHelpers
open FsProjLint.Tests.GitFixtures
open FsProjLint.Tests.TestFixtures

let private withProperties (properties: string) =
    sprintf "<Project><PropertyGroup>%s</PropertyGroup></Project>" properties

let private loadOk (dir: string) (relativePath: string) =
    match load dir (Path.Combine(dir, relativePath)) with
    | Ok project -> project
    | Error e -> failwith e

let private valueOf (project: Project) (name: string) =
    property project name |> Option.map (fun found -> found.Value)

// -- property --

[<Fact>]
let ``property reads the project's own declaration`` () =
    let project = projectOf (withProperties "<Version>1.0.0</Version>")

    test <@ property project "Version" = Some { Value = "1.0.0"; File = project.Path } @>

[<Fact>]
let ``property is None when neither the project nor a props file sets it`` () =
    test <@ property (projectOf (withProperties "")) "Version" = None @>

[<Fact>]
let ``property falls back to the nearest Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" (withProperties "<Authors>someone</Authors>")
        writeFile dir "src/Lib/Lib.fsproj" (withProperties "")

        let project = loadOk dir "src/Lib/Lib.fsproj"

        test
            <@
                property project "Authors" =
                    Some
                        {
                            Value = "someone"
                            File = Path.Combine(dir, "Directory.Build.props")
                        }
            @>)

[<Fact>]
let ``the project's declaration overrides Directory.Build.props, even when empty`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            (withProperties "<Authors>props</Authors><Description>props</Description>")

        writeFile dir "Lib.fsproj" (withProperties "<Authors>project</Authors><Description></Description>")

        let project = loadOk dir "Lib.fsproj"

        test <@ valueOf project "Authors" = Some "project" @>
        test <@ valueOf project "Description" = None @>)

[<Fact>]
let ``only the nearest Directory.Build.props counts, as MSBuild imports only that one`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" (withProperties "<Authors>root</Authors>")
        writeFile dir "src/Directory.Build.props" (withProperties "<Description>src</Description>")
        writeFile dir "src/Lib/Lib.fsproj" (withProperties "")

        let project = loadOk dir "src/Lib/Lib.fsproj"

        test <@ valueOf project "Description" = Some "src" @>
        test <@ valueOf project "Authors" = None @>)

[<Fact>]
let ``a Directory.Build.props above the repository root is not read`` () =
    withTempDir (fun outer ->
        writeFile outer "Directory.Build.props" (withProperties "<Authors>outside</Authors>")
        let repo = Path.Combine(outer, "repo")
        writeFile repo "Lib.fsproj" (withProperties "")

        test <@ (loadOk repo "Lib.fsproj").DirectoryBuildProps = None @>)

[<Fact>]
let ``load reports a project that does not parse`` () =
    withTempDir (fun dir ->
        writeFile dir "Bad.fsproj" "<Project><Unclosed>"

        test <@ Result.isError (load dir (Path.Combine(dir, "Bad.fsproj"))) @>)

[<Fact>]
let ``load reports a Directory.Build.props that does not parse`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" "<Project><Unclosed>"
        writeFile dir "Lib.fsproj" (withProperties "")

        match load dir (Path.Combine(dir, "Lib.fsproj")) with
        | Error e -> test <@ e.Contains "Directory.Build.props" @>
        | Ok _ -> failwith "expected the props parse failure")

// -- hasPackageReference --

let private withReference (reference: string) =
    sprintf "<Project><ItemGroup>%s</ItemGroup></Project>" reference

[<Fact>]
let ``hasPackageReference finds a reference in the project`` () =
    test <@ hasPackageReference (projectOf (withReference """<PackageReference Include="Some" />""")) "Some" @>

[<Fact>]
let ``hasPackageReference finds a reference in Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" (withReference """<PackageReference Include="Some" />""")
        writeFile dir "Lib.fsproj" (withProperties "")

        test <@ hasPackageReference (loadOk dir "Lib.fsproj") "Some" @>)

[<Theory>]
[<InlineData("""<PackageReference Include="Other" />""")>]
[<InlineData("""<PackageReference Update="Some" />""")>]
[<InlineData("")>]
let ``hasPackageReference is false without an Include of that package`` (reference: string) =
    test <@ not (hasPackageReference (projectOf (withReference reference)) "Some") @>

// -- isPackable --

[<Theory>]
[<InlineData("<PackageId>P</PackageId>", true)>]
[<InlineData("<PackageId>P</PackageId><IsPackable>true</IsPackable>", true)>]
[<InlineData("<PackageId>P</PackageId><OutputType>Library</OutputType>", true)>]
[<InlineData("<PackageId>P</PackageId><OutputType>Exe</OutputType><PackAsTool>true</PackAsTool>", true)>]
[<InlineData("<PackageId>P</PackageId><IsPackable>false</IsPackable>", false)>]
[<InlineData("<PackageId>P</PackageId><IsPackable>False</IsPackable>", false)>]
[<InlineData("<PackageId>P</PackageId><OutputType>Exe</OutputType>", false)>]
[<InlineData("<OutputType>Exe</OutputType><PackAsTool>true</PackAsTool>", false)>]
[<InlineData("<PackageId></PackageId>", false)>]
[<InlineData("", false)>]
let ``isPackable: a PackageId, not IsPackable false, and not an example exe`` (properties: string, expected: bool) =
    test <@ isPackable (projectOf (withProperties properties)) = expected @>

[<Fact>]
let ``isPackable honours IsPackable false from a tests Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile dir "tests/Directory.Build.props" (withProperties "<IsPackable>false</IsPackable>")
        writeFile dir "tests/Lib.Tests/Lib.Tests.fsproj" (withProperties "<PackageId>Lib.Tests</PackageId>")

        test <@ not (isPackable (loadOk dir "tests/Lib.Tests/Lib.Tests.fsproj")) @>)

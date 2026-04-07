module FsProjLint.Tests.RepoCheckTests

open System.IO
open Xunit
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers

let private createFile (dir: string) (relativePath: string) =
    let fullPath = Path.Combine(dir, relativePath)
    let parent = Path.GetDirectoryName(fullPath)
    Directory.CreateDirectory(parent) |> ignore
    File.WriteAllText(fullPath, "")

[<Fact>]
let ``checkRepo passes with all required files`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true []

        test <@ results |> List.forall (fun r -> r.Passed) @>)

[<Fact>]
let ``checkRepo fails when LICENSE missing`` () =
    withTempDir (fun dir ->
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true []
        let licenseCheck = results |> List.find (fun r -> r.Name = "LICENSE exists")

        test <@ not licenseCheck.Passed @>)

[<Fact>]
let ``checkRepo passes with LICENSE.md instead of LICENSE`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE.md"
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true []
        let licenseCheck = results |> List.find (fun r -> r.Name = "LICENSE exists")

        test <@ licenseCheck.Passed @>)

[<Fact>]
let ``checkRepo fails when README missing`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir ".editorconfig"

        let results = checkRepo dir false []
        let readmeCheck = results |> List.find (fun r -> r.Name = "README.md exists")

        test <@ not readmeCheck.Passed @>)

[<Fact>]
let ``checkRepo fails when editorconfig missing`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"

        let results = checkRepo dir false []

        let editorconfigCheck =
            results |> List.find (fun r -> r.Name = ".editorconfig exists")

        test <@ not editorconfigCheck.Passed @>)

[<Fact>]
let ``checkRepo skips docs/index.md when no packable projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"

        let results = checkRepo dir false []

        let docsCheck = results |> List.tryFind (fun r -> r.Name = "docs/index.md exists")

        test <@ docsCheck.IsNone @>)

[<Fact>]
let ``checkRepo checks docs-index.html links for packable projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        File.WriteAllText(
            Path.Combine(dir, "docs", "docs-index.html"),
            """<a href="MyLib/">MyLib</a>"""
        )

        let results = checkRepo dir true [ "MyLib"; "MissingLib" ]
        let myLibCheck = results |> List.find (fun r -> r.Name = "docs-index.html links to MyLib")
        let missingCheck = results |> List.find (fun r -> r.Name = "docs-index.html links to MissingLib")

        test <@ myLibCheck.Passed @>
        test <@ not missingCheck.Passed @>)

[<Fact>]
let ``checkRepo skips docs-index.html checks when file missing`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true [ "MyLib" ]
        let indexHtmlChecks = results |> List.filter (fun r -> r.Name.Contains("docs-index.html"))

        test <@ indexHtmlChecks |> List.isEmpty @>)

[<Fact>]
let ``checkRepo fails when docs/index.md missing but has packable projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"

        let results = checkRepo dir true []

        let docsCheck = results |> List.find (fun r -> r.Name = "docs/index.md exists")

        test <@ not docsCheck.Passed @>)

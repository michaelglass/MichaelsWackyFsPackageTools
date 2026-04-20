module FsProjLint.Tests.RepoCheckTests

open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers

let private isPassed (result: CheckResult) =
    match result.Outcome with
    | Passed -> true
    | Failed _ -> false

let private isFailed (result: CheckResult) =
    match result.Outcome with
    | Passed -> false
    | Failed _ -> true

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

        let results = checkRepo dir true

        test <@ results |> List.forall isPassed @>)

[<Fact>]
let ``checkRepo fails when LICENSE missing`` () =
    withTempDir (fun dir ->
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true
        let licenseCheck = results |> List.find (fun r -> r.Name = "LICENSE exists")

        test <@ isFailed licenseCheck @>)

[<Fact>]
let ``checkRepo passes with LICENSE.md instead of LICENSE`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE.md"
        createFile dir "README.md"
        createFile dir ".editorconfig"
        createFile dir "docs/index.md"

        let results = checkRepo dir true
        let licenseCheck = results |> List.find (fun r -> r.Name = "LICENSE exists")

        test <@ isPassed licenseCheck @>)

[<Fact>]
let ``checkRepo fails when README missing`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir ".editorconfig"

        let results = checkRepo dir false
        let readmeCheck = results |> List.find (fun r -> r.Name = "README.md exists")

        test <@ isFailed readmeCheck @>)

[<Fact>]
let ``checkRepo fails when editorconfig missing`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"

        let results = checkRepo dir false

        let editorconfigCheck =
            results |> List.find (fun r -> r.Name = ".editorconfig exists")

        test <@ isFailed editorconfigCheck @>)

[<Fact>]
let ``checkRepo skips docs/index.md when no packable projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"

        let results = checkRepo dir false

        let docsCheck = results |> List.tryFind (fun r -> r.Name = "docs/index.md exists")

        test <@ docsCheck.IsNone @>)

[<Fact>]
let ``checkRepo fails when docs/index.md missing but has packable projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE"
        createFile dir "README.md"
        createFile dir ".editorconfig"

        let results = checkRepo dir true

        let docsCheck = results |> List.find (fun r -> r.Name = "docs/index.md exists")

        test <@ isFailed docsCheck @>)

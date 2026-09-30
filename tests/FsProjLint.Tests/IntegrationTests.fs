module FsProjLint.Tests.IntegrationTests

open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers
open FsProjLint.Tests.TestFixtures
open FsProjLint.Tests.GitFixtures

let private isPassed (result: CheckResult) = CheckOutcome.isPassed result.Outcome

let private isFailed (result: CheckResult) = CheckOutcome.isFailed result.Outcome

let private createFile (dir: string) (relativePath: string) (content: string) =
    let fullPath = Path.Combine(dir, relativePath)
    let parent = Path.GetDirectoryName(fullPath)
    Directory.CreateDirectory(parent) |> ignore
    File.WriteAllText(fullPath, content)

// -- discoverProjects --

[<Fact>]
let ``discoverProjects returns empty list when no src directory`` () =
    withTempDir (fun dir ->
        let results = discoverProjects dir

        test <@ List.isEmpty results @>)

[<Fact>]
let ``discoverProjects finds nested projects`` () =
    withTempDir (fun dir ->
        createFile dir "src/A/A.fsproj" "<Project />"
        createFile dir "src/B/Sub/B.fsproj" "<Project />"

        let results = discoverProjects dir

        test <@ results.Length = 2 @>)

[<Fact>]
let ``discoverProjects returns sorted list`` () =
    withTempDir (fun dir ->
        createFile dir "src/Zebra/Zebra.fsproj" "<Project />"
        createFile dir "src/Alpha/Alpha.fsproj" "<Project />"

        let results = discoverProjects dir
        let names = results |> List.map Path.GetFileName

        test <@ names = [ "Alpha.fsproj"; "Zebra.fsproj" ] @>)

[<Fact>]
let ``discoverProjects skips build output, dot-directories and nested checkouts`` () =
    withTempDir (fun dir ->
        let real =
            layOutNestedCheckouts dir (fun at -> File.WriteAllText(Path.Combine(at, "Real.fsproj"), "<Project />"))

        test <@ discoverProjects dir = [ Path.Combine(real, "Real.fsproj") ] @>)

// -- runLint integration --

[<Fact>]
let ``complete valid repo passes all checks`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE" ""
        createFile dir "README.md" ""
        createFile dir ".editorconfig" ""
        createFile dir "docs/index.md" ""
        createFile dir "src/MyProject/MyProject.fsproj" packableFsproj
        // The RefStamp local-pack guard, wired the one-line-per-repo way.
        createFile
            dir
            "Directory.Build.props"
            """<Project>
  <ItemGroup>
    <PackageReference Include="RefStamp" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>"""
        // An origin remote naming the repository packableFsproj's RepositoryUrl names.
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        git dir [ "remote"; "add"; "origin"; "git@github.com:test/test.git" ] |> ignore

        let result = runLint dir
        let allChecks = result.RepoChecks @ (result.ProjectChecks |> List.collect snd)

        test <@ allChecks |> List.forall isPassed @>)

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
        let failed = allChecks |> List.filter isFailed

        test <@ failed.Length > 0 @>

        let failedNames = failed |> List.map (fun c -> c.Name)

        test <@ failedNames |> List.contains "LICENSE exists" @>
        test <@ failedNames |> List.contains "README.md exists" @>
        test <@ failedNames |> List.contains ".editorconfig exists" @>
        test <@ failedNames |> List.contains "TreatWarningsAsErrors is true" @>
        test <@ failedNames |> List.contains "Description present" @>
        // Packable repo with no RefStamp wiring: local packs are unguarded.
        test <@ failedNames |> List.contains "Local packs are ref-stamped (RefStamp)" @>)

[<Fact>]
let ``runLint with no projects found`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE" ""
        createFile dir "README.md" ""
        createFile dir ".editorconfig" ""

        let result = runLint dir

        test <@ List.isEmpty result.ProjectChecks @>
        // 3 base repo checks (LICENSE, README, .editorconfig) + the
        // gitignore-leak check (passes: temp dir is not a git repo).
        test <@ result.RepoChecks.Length = 4 @>)

[<Fact>]
let ``runLint with mixed passing and failing projects`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE" ""
        createFile dir "README.md" ""
        createFile dir ".editorconfig" ""
        createFile dir "docs/index.md" ""
        createFile dir "src/GoodProject/GoodProject.fsproj" packableFsproj

        let badFsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>BadPackage</PackageId>
  </PropertyGroup>
</Project>"""

        createFile dir "src/BadProject/BadProject.fsproj" badFsproj
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        git dir [ "remote"; "add"; "origin"; "https://github.com/test/test" ] |> ignore

        let result = runLint dir

        test <@ result.ProjectChecks.Length = 2 @>

        let goodChecks =
            result.ProjectChecks
            |> List.find (fun (p, _) -> p.Contains("GoodProject"))
            |> snd

        let badChecks =
            result.ProjectChecks
            |> List.find (fun (p, _) -> p.Contains("BadProject"))
            |> snd

        test <@ goodChecks |> List.forall isPassed @>
        test <@ badChecks |> List.exists isFailed @>)

[<Fact>]
let ``runLint with malformed XML produces failure result instead of exception`` () =
    withTempDir (fun dir ->
        createFile dir "LICENSE" ""
        createFile dir "README.md" ""
        createFile dir ".editorconfig" ""
        createFile dir "src/Bad/Bad.fsproj" "this is not valid xml <><>"

        let result = runLint dir

        test <@ result.ProjectChecks.Length = 1 @>

        let (_, checks) = result.ProjectChecks.[0]

        test <@ checks.Length = 1 @>
        test <@ checks.[0].Name = "XML parse" @>
        test <@ isFailed checks.[0] @>)

// -- Program.main --

[<Fact>]
let ``Program.main returns 0 for help flag`` () =
    let result = FsProjLint.Program.main [| "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``Program.main returns 1 for unknown flag`` () =
    let result = FsProjLint.Program.main [| "--bogus" |]

    test <@ result = 1 @>

[<Fact>]
let ``Program.main returns 0 for --version`` () =
    let printed, result =
        withCapturedConsole (fun () -> FsProjLint.Program.main [| "--version" |])

    test <@ result = 0 @>
    test <@ printed.Contains "fsprojlint" @>

[<Fact>]
let ``Program.main returns 0 for -h`` () =
    let result = FsProjLint.Program.main [| "-h" |]

    test <@ result = 0 @>

[<Fact>]
let ``Program.main returns 0 for help`` () =
    let result = FsProjLint.Program.main [| "help" |]

    test <@ result = 0 @>

[<Fact>]
let ``Program.main returns 0 for --help`` () =
    let result = FsProjLint.Program.main [| "--help" |]

    test <@ result = 0 @>

[<Fact>]
let ``Program.main returns 1 for failing repo`` () =
    withTempDir (fun tmpDir ->
        let prev = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory(tmpDir)
            let result = FsProjLint.Program.main [||]
            // Empty directory has no LICENSE, README, etc.
            test <@ result = 1 @>
        finally
            Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``Program.main returns 0 for fully passing repo`` () =
    withTempDir (fun tmpDir ->
        createFile tmpDir "LICENSE" ""
        createFile tmpDir "README.md" ""
        createFile tmpDir ".editorconfig" ""
        createFile tmpDir "docs/index.md" ""
        createFile tmpDir "src/MyProject/MyProject.fsproj" packableFsproj
        // The RefStamp local-pack guard, wired the one-line-per-repo way.
        createFile
            tmpDir
            "Directory.Build.props"
            """<Project>
  <ItemGroup>
    <PackageReference Include="RefStamp" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>"""

        let prev = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory(tmpDir)

            let printed, result = withCapturedConsole (fun () -> FsProjLint.Program.main [||])
            test <@ result = 0 @>
            test <@ printed.Contains "Passed:" @>
            test <@ not (printed.Contains "FAILED:") @>
        finally
            Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``Program.main prints FAILED and Passed sections for mixed results`` () =
    withTempDir (fun tmpDir ->
        // Missing LICENSE and editorconfig but has README
        createFile tmpDir "README.md" ""

        let prev = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory(tmpDir)

            let printed, result = withCapturedConsole (fun () -> FsProjLint.Program.main [||])
            test <@ result = 1 @>
            test <@ printed.Contains "FAILED:" @>
            test <@ printed.Contains "FAIL" @>
            test <@ printed.Contains "Passed:" @>
            test <@ printed.Contains "PASS" @>
            test <@ printed.Contains "Result:" @>
        finally
            Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``Program.main prints the failure reason and the skipped section`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore

        git dir [ "remote"; "add"; "origin"; "https://github.com/michaelglass/UnionConfig.git" ]
        |> ignore

        writeFile
            dir
            "src/Mismatched/Mismatched.fsproj"
            (packableFsproj.Replace("https://github.com/test/test", "https://github.com/michaelglass/union-config"))

        writeFile
            dir
            "src/Unresolved/Unresolved.fsproj"
            (packableFsproj.Replace("https://github.com/test/test", "$(Url)"))

        let prev = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory(dir)
            let printed, exitCode = withCapturedConsole (fun () -> FsProjLint.Program.main [||])

            test <@ exitCode = 1 @>

            test
                <@
                    printed.Contains(
                        sprintf
                            "FAIL %s (%s)"
                            "RepositoryUrl matches origin remote"
                            (Path.Combine("src", "Mismatched", "Mismatched.fsproj"))
                    )
                @>

            test <@ printed.Contains "<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>" @>
            test <@ printed.Contains "Skipped:" @>

            test
                <@
                    printed.Contains(
                        sprintf
                            "SKIP %s (%s)"
                            "RepositoryUrl matches origin remote"
                            (Path.Combine("src", "Unresolved", "Unresolved.fsproj"))
                    )
                @>

            test <@ printed.Contains "1 skipped" @>
        finally
            Directory.SetCurrentDirectory(prev))

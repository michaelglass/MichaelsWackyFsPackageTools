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

// -- discoverProjects --

let private discoveredPaths (dir: string) =
    discoverProjects dir |> List.map (fst >> fun p -> Path.GetRelativePath(dir, p))

let private libraryFsproj (packageId: string) (extra: string) =
    sprintf "<Project><PropertyGroup><PackageId>%s</PackageId>%s</PropertyGroup></Project>" packageId extra

[<Fact>]
let ``discoverProjects returns empty list when the repository has no projects`` () =
    withTempDir (fun dir ->
        let results = discoverProjects dir

        test <@ List.isEmpty results @>)

[<Fact>]
let ``discoverProjects finds nested projects`` () =
    withTempDir (fun dir ->
        writeFile dir "src/A/A.fsproj" "<Project />"
        writeFile dir "src/B/Sub/B.fsproj" "<Project />"

        let results = discoverProjects dir

        test <@ results.Length = 2 @>)

[<Fact>]
let ``discoverProjects returns sorted list`` () =
    withTempDir (fun dir ->
        writeFile dir "src/Zebra/Zebra.fsproj" "<Project />"
        writeFile dir "src/Alpha/Alpha.fsproj" "<Project />"

        let names = discoverProjects dir |> List.map (fst >> Path.GetFileName)

        test <@ names = [ "Alpha.fsproj"; "Zebra.fsproj" ] @>)

[<Fact>]
let ``discoverProjects skips build output, dot-directories and nested checkouts`` () =
    withTempDir (fun dir ->
        let real =
            layOutNestedCheckouts dir (fun at -> File.WriteAllText(Path.Combine(at, "Real.fsproj"), "<Project />"))

        test <@ discoverProjects dir |> List.map fst = [ Path.Combine(real, "Real.fsproj") ] @>)

[<Fact>]
let ``discoverProjects finds a packable project at the repository root`` () =
    withTempDir (fun dir ->
        writeFile dir "Shim.fsproj" (libraryFsproj "Shim" "")

        test <@ discoveredPaths dir = [ "Shim.fsproj" ] @>)

[<Fact>]
let ``discoverProjects finds packable projects outside src`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "tools/Tool/Tool.fsproj"
            (libraryFsproj "Tool" "<OutputType>Exe</OutputType><PackAsTool>true</PackAsTool>")

        writeFile dir "src/Lib/Lib.fsproj" (libraryFsproj "Lib" "")

        test
            <@
                discoveredPaths dir =
                    [
                        Path.Combine("src", "Lib", "Lib.fsproj")
                        Path.Combine("tools", "Tool", "Tool.fsproj")
                    ]
            @>)

[<Fact>]
let ``discoverProjects leaves out test, benchmark and example projects outside src`` () =
    withTempDir (fun dir ->
        writeFile dir "tests/Lib.Tests/Lib.Tests.fsproj" (libraryFsproj "Lib.Tests" "<IsPackable>false</IsPackable>")

        writeFile
            dir
            "tests/Directory.Build.props"
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"

        writeFile dir "tests/Other.Tests/Other.Tests.fsproj" (libraryFsproj "Other.Tests" "")
        writeFile dir "benchmarks/Bench/Bench.fsproj" (libraryFsproj "Bench" "<OutputType>Exe</OutputType>")

        writeFile
            dir
            "examples/Sample/Sample.fsproj"
            "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"

        test <@ List.isEmpty (discoverProjects dir) @>)

[<Fact>]
let ``discoverProjects keeps every project under src, packable or not`` () =
    withTempDir (fun dir ->
        writeFile dir "src/Lib/Lib.fsproj" (libraryFsproj "Lib" "")

        writeFile
            dir
            "src/Lib.Tests/Lib.Tests.fsproj"
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"

        writeFile dir "src/Sample/Sample.fsproj" "<Project />"

        test
            <@
                discoveredPaths dir =
                    [
                        Path.Combine("src", "Lib.Tests", "Lib.Tests.fsproj")
                        Path.Combine("src", "Lib", "Lib.fsproj")
                        Path.Combine("src", "Sample", "Sample.fsproj")
                    ]
            @>)

[<Fact>]
let ``discoverProjects reports a project outside src that does not parse`` () =
    withTempDir (fun dir ->
        writeFile dir "Broken.fsproj" "<Project"

        match discoverProjects dir with
        | [ (path, Error _) ] -> test <@ Path.GetFileName path = "Broken.fsproj" @>
        | other -> failwithf "expected one parse failure, got %A" other)

// -- runLint integration --

[<Fact>]
let ``complete valid repo passes all checks`` () =
    withTempDir (fun dir ->
        writeFile dir "LICENSE" ""
        writeFile dir "README.md" ""
        writeFile dir ".editorconfig" ""
        writeFile dir "docs/index.md" ""
        writeFile dir "src/MyProject/MyProject.fsproj" packableFsproj
        // The RefStamp local-pack guard, wired the one-line-per-repo way.
        writeFile
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

        writeFile dir "src/MyProject/MyProject.fsproj" fsproj

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
        writeFile dir "LICENSE" ""
        writeFile dir "README.md" ""
        writeFile dir ".editorconfig" ""

        let result = runLint dir

        test <@ List.isEmpty result.ProjectChecks @>
        // 3 base repo checks (LICENSE, README, .editorconfig) + the
        // gitignore-leak check (passes: temp dir is not a git repo).
        test <@ result.RepoChecks.Length = 4 @>)

[<Fact>]
let ``runLint with mixed passing and failing projects`` () =
    withTempDir (fun dir ->
        writeFile dir "LICENSE" ""
        writeFile dir "README.md" ""
        writeFile dir ".editorconfig" ""
        writeFile dir "docs/index.md" ""
        writeFile dir "src/GoodProject/GoodProject.fsproj" packableFsproj

        let badFsproj =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>BadPackage</PackageId>
  </PropertyGroup>
</Project>"""

        writeFile dir "src/BadProject/BadProject.fsproj" badFsproj
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
        writeFile dir "LICENSE" ""
        writeFile dir "README.md" ""
        writeFile dir ".editorconfig" ""
        writeFile dir "src/Bad/Bad.fsproj" "this is not valid xml <><>"

        let result = runLint dir

        test <@ result.ProjectChecks.Length = 1 @>

        let (_, checks) = result.ProjectChecks.[0]

        test <@ checks.Length = 1 @>
        test <@ checks.[0].Name = "XML parse" @>
        test <@ isFailed checks.[0] @>)

let private reasonOf (check: CheckResult) =
    match check.Outcome with
    | Failed reason
    | Skipped reason -> reason
    | Passed -> ""

[<Fact>]
let ``runLint fails a Directory.Build.props that does not parse once, and skips each project under it`` () =
    withTempDir (fun dir ->
        writeFile dir "src/Directory.Build.props" "<Project"
        writeFile dir "src/A/A.fsproj" "<Project />"
        writeFile dir "src/B/B.fsproj" "<Project />"
        writeFile dir "tools/Fine/Fine.fsproj" (libraryFsproj "Fine" "")

        let result = runLint dir

        match result.PropsChecks with
        | [ (props, check) ] ->
            test <@ props = Path.Combine(dir, "src", "Directory.Build.props") @>
            test <@ check.Name = "XML parse" && isFailed check @>
            test <@ (reasonOf check).StartsWith "Failed to parse Directory.Build.props: " @>
        | other -> failwithf "expected one props failure, got %A" other

        let underProps =
            result.ProjectChecks
            |> List.filter (fun (path, _) -> not (path.Contains "Fine"))

        test <@ underProps.Length = 2 @>

        for _, checks in underProps do
            match checks with
            | [ check ] ->
                test <@ CheckOutcome.isSkipped check.Outcome @>

                test
                    <@
                        reasonOf check =
                            sprintf
                                "%s does not parse, so this project's properties cannot be read"
                                (Path.Combine("src", "Directory.Build.props"))
                    @>
            | other -> failwithf "expected one skipped check, got %A" other

        // A project outside the broken props file is still checked.
        let fine =
            result.ProjectChecks |> List.find (fun (path, _) -> path.Contains "Fine") |> snd

        test <@ fine |> List.exists (fun c -> c.Name = "TreatWarningsAsErrors is true") @>)

[<Fact>]
let ``runLint reports a project that does not parse as its own failure, not its props file's`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" "<Project><PropertyGroup /></Project>"
        writeFile dir "src/Bad/Bad.fsproj" "<Project"

        let result = runLint dir

        test <@ List.isEmpty result.PropsChecks @>

        match result.ProjectChecks with
        | [ (_, [ check ]) ] ->
            test <@ check.Name = "XML parse" && isFailed check @>
            test <@ (reasonOf check).StartsWith "Failed to parse Bad.fsproj: " @>
        | other -> failwithf "expected one parse failure, got %A" other)

[<Fact>]
let ``runLint runs the package checks on a packable project at the repository root`` () =
    withTempDir (fun dir ->
        writeFile dir "Shim.fsproj" packableFsproj

        match (runLint dir).ProjectChecks with
        | [ (path, checks) ] ->
            test <@ Path.GetFileName path = "Shim.fsproj" @>
            test <@ checks |> List.exists (fun c -> c.Name = "Description present" && isPassed c) @>
        | other -> failwithf "expected the root project, got %A" other)

[<Fact>]
let ``runLint counts package metadata set only in Directory.Build.props as present`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            """<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
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

        writeFile
            dir
            "src/Lib/Lib.fsproj"
            "<Project><PropertyGroup><PackageId>Lib</PackageId></PropertyGroup></Project>"

        let checks = (runLint dir).ProjectChecks |> List.collect snd
        // No origin remote here, so the origin comparison is skipped; every
        // other check passes on values read from Directory.Build.props.
        let notPassed =
            checks |> List.filter (isPassed >> not) |> List.map (fun c -> c.Name)

        test <@ notPassed = [ "RepositoryUrl matches origin remote" ] @>
        test <@ checks.Length = 12 @>)

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
        writeFile tmpDir "LICENSE" ""
        writeFile tmpDir "README.md" ""
        writeFile tmpDir ".editorconfig" ""
        writeFile tmpDir "docs/index.md" ""
        writeFile tmpDir "src/MyProject/MyProject.fsproj" packableFsproj
        // The RefStamp local-pack guard, wired the one-line-per-repo way.
        writeFile
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
let ``Program.main fails a broken Directory.Build.props once and skips the projects under it`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "LICENSE" ""
        writeFile tmpDir "README.md" ""
        writeFile tmpDir ".editorconfig" ""
        writeFile tmpDir "src/Directory.Build.props" "<Project"
        writeFile tmpDir "src/A/A.fsproj" "<Project />"
        writeFile tmpDir "src/B/B.fsproj" "<Project />"

        let prev = Directory.GetCurrentDirectory()

        try
            Directory.SetCurrentDirectory(tmpDir)

            let printed, result = withCapturedConsole (fun () -> FsProjLint.Program.main [||])
            let lines = printed.Split('\n') |> Array.map _.TrimEnd('\r')
            let props = Path.Combine("src", "Directory.Build.props")

            test <@ result = 1 @>
            test <@ lines |> Array.filter _.StartsWith("  FAIL ") = [| sprintf "  FAIL XML parse (%s)" props |] @>

            test
                <@
                    lines |> Array.filter _.StartsWith("  SKIP ") =
                        [|
                            sprintf "  SKIP Project checks (%s)" (Path.Combine("src", "A", "A.fsproj"))
                            sprintf "  SKIP Project checks (%s)" (Path.Combine("src", "B", "B.fsproj"))
                        |]
                @>

            test <@ printed.Contains(sprintf "%s does not parse, so this project's properties cannot be read" props) @>
        finally
            Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``Program.main prints FAILED and Passed sections for mixed results`` () =
    withTempDir (fun tmpDir ->
        // Missing LICENSE and editorconfig but has README
        writeFile tmpDir "README.md" ""

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

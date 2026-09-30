module FsProjLint.Tests.RepositoryUrlTests

// A packable project's RepositoryUrl (and a github.com PackageProjectUrl) must
// name the repository the project lives in, as recorded by its `origin` remote.
// nuget.org links a package to its RepositoryUrl; UnionConfig shipped with
// `https://github.com/michaelglass/union-config` while the repository is
// github.com/michaelglass/UnionConfig, so that link was a 404.

open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers
open FsProjLint.Tests.GitFixtures

let private repoCheck = "RepositoryUrl matches origin remote"
let private projectUrlCheck = "PackageProjectUrl matches origin remote"

let private parse (url: string) =
    match RepoRef.tryParse url with
    | Some r -> r
    | None -> failwithf "expected %s to parse" url

let private reason (result: CheckResult) =
    match result.Outcome with
    | Failed r
    | Skipped r -> r
    | Passed -> ""

let private fsproj (properties: string) =
    sprintf
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
%s
  </PropertyGroup>
</Project>"""
        properties

let private props (properties: string) =
    sprintf
        """<Project>
  <PropertyGroup>
%s
  </PropertyGroup>
</Project>"""
        properties

let private originOf (url: string) = Origin(parse url)

/// Write `src/MyPackage/MyPackage.fsproj` under `dir` and run the check on it.
let private checkWith (dir: string) (origin: OriginRemote) (projectXml: string) =
    let projectPath = Path.Combine(dir, "src", "MyPackage", "MyPackage.fsproj")
    writeFile dir "src/MyPackage/MyPackage.fsproj" projectXml

    match Shared.MsBuildProject.load dir projectPath with
    | Ok project -> checkRepositoryUrls origin dir project
    | Error e -> failwith e

let private find (name: string) (results: CheckResult list) =
    results |> List.find (fun r -> r.Name = name)

// -- RepoRef.tryParse --

[<Theory>]
[<InlineData("https://github.com/michaelglass/UnionConfig")>]
[<InlineData("https://github.com/michaelglass/UnionConfig/")>]
[<InlineData("https://github.com/michaelglass/UnionConfig.git")>]
[<InlineData("https://github.com/michaelglass/UnionConfig.git/")>]
[<InlineData("http://github.com/michaelglass/UnionConfig")>]
[<InlineData("git@github.com:michaelglass/UnionConfig.git")>]
[<InlineData("git@github.com:michaelglass/UnionConfig")>]
[<InlineData("ssh://git@github.com/michaelglass/UnionConfig.git")>]
[<InlineData("ssh://git@github.com:22/michaelglass/UnionConfig.git")>]
[<InlineData("https://user@github.com/michaelglass/UnionConfig")>]
[<InlineData("git://github.com/michaelglass/UnionConfig.git")>]
[<InlineData("  https://github.com/michaelglass/UnionConfig  ")>]
let ``tryParse reads host, owner and name from every URL spelling`` (url: string) =
    let parsed = parse url
    test <@ (parsed.Host, parsed.Owner, parsed.Name) = ("github.com", "michaelglass", "UnionConfig") @>

[<Fact>]
let ``tryParse keeps nested groups in the owner`` () =
    let parsed = parse "https://GitLab.com/group/sub/repo.git"
    test <@ (parsed.Host, parsed.Owner, parsed.Name) = ("gitlab.com", "group/sub", "repo") @>

[<Fact>]
let ``tryParse takes only owner and name from a deeper github.com path`` () =
    let parsed = parse "https://github.com/michaelglass/UnionConfig/tree/main/docs"
    test <@ (parsed.Owner, parsed.Name) = ("michaelglass", "UnionConfig") @>

[<Theory>]
[<InlineData("")>]
[<InlineData("not a url")>]
[<InlineData("$(RepositoryUrl)")>]
[<InlineData("https://github.com/$(Owner)/repo")>]
[<InlineData("https://github.com/michaelglass")>]
[<InlineData("https://github.com/")>]
[<InlineData("file:///tmp/repo.git")>]
[<InlineData("/tmp/repo.git")>]
[<InlineData("../repo.git")>]
let ``tryParse rejects what is not a hosted repository URL`` (url: string) = test <@ RepoRef.tryParse url = None @>

// -- RepoRef.sameRepository --

[<Fact>]
let ``ssh and https spellings of one repository match`` () =
    let ssh = parse "git@github.com:michaelglass/UnionConfig.git"
    let https = parse "https://github.com/michaelglass/UnionConfig/"
    test <@ RepoRef.sameRepository ssh https @>

[<Fact>]
let ``github.com owner and repository names match case-insensitively`` () =
    let origin = parse "git@github.com:michaelglass/FSharpLintAnalyzerShim.git"
    let declared = parse "https://GitHub.com/MichaelGlass/FsharpLintAnalyzerShim"
    test <@ RepoRef.sameRepository origin declared @>

[<Fact>]
let ``a hyphenated name is a different repository`` () =
    let origin = parse "git@github.com:michaelglass/UnionConfig.git"
    let declared = parse "https://github.com/michaelglass/union-config"
    test <@ not (RepoRef.sameRepository origin declared) @>

[<Fact>]
let ``a different owner is a different repository`` () =
    let origin = parse "https://github.com/michaelglass/UnionConfig"
    let declared = parse "https://github.com/someoneelse/UnionConfig"
    test <@ not (RepoRef.sameRepository origin declared) @>

[<Fact>]
let ``a different host is a different repository`` () =
    let origin = parse "https://github.com/michaelglass/UnionConfig"
    let declared = parse "https://gitlab.com/michaelglass/UnionConfig"
    test <@ not (RepoRef.sameRepository origin declared) @>

[<Fact>]
let ``names on hosts other than github.com compare case-sensitively`` () =
    let origin = parse "https://git.example.com/team/Repo"
    let declared = parse "https://git.example.com/team/repo"
    test <@ not (RepoRef.sameRepository origin declared) @>

[<Fact>]
let ``toUrl renders the https URL`` () =
    test
        <@
            RepoRef.toUrl (parse "git@github.com:michaelglass/UnionConfig.git") =
                "https://github.com/michaelglass/UnionConfig"
        @>

// -- resolveOrigin --

[<Fact>]
let ``resolveOrigin reports a directory that is not a repository`` () =
    withTempDir (fun dir ->
        match resolveOrigin dir with
        | NoOrigin reason -> test <@ reason.Contains "not a git or jj repository" @>
        | Origin r -> failwithf "expected no origin, got %A" r)

[<Fact>]
let ``resolveOrigin reads origin from a plain git repository`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore

        git dir [ "remote"; "add"; "origin"; "git@github.com:michaelglass/UnionConfig.git" ]
        |> ignore

        test <@ resolveOrigin dir = originOf "https://github.com/michaelglass/UnionConfig" @>)

[<Fact>]
let ``resolveOrigin reports a repository without an origin remote`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore

        git dir [ "remote"; "add"; "upstream"; "https://github.com/michaelglass/UnionConfig" ]
        |> ignore

        match resolveOrigin dir with
        | NoOrigin reason -> test <@ reason.Contains "no `origin` remote" @>
        | Origin r -> failwithf "expected no origin, got %A" r)

[<Fact>]
let ``resolveOrigin reports an origin that is not a hosted repository URL`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q"; "-b"; "main" ] |> ignore
        git dir [ "remote"; "add"; "origin"; "/srv/git/local.git" ] |> ignore

        match resolveOrigin dir with
        | NoOrigin reason -> test <@ reason.Contains "/srv/git/local.git" @>
        | Origin r -> failwithf "expected no origin, got %A" r)

[<Fact>]
let ``resolveOrigin reads origin from a jj store with no colocated .git`` () =
    withTempDir (fun root ->
        let store = initFakeJjStore root

        gitStore store root [ "remote"; "add"; "origin"; "https://github.com/michaelglass/UnionConfig.git" ]
        |> ignore

        test <@ not (Directory.Exists(Path.Combine(root, ".git"))) @>
        test <@ resolveOrigin root = originOf "https://github.com/michaelglass/UnionConfig" @>)

[<Fact>]
let ``resolveOrigin reads origin from a secondary jj workspace`` () =
    // `jj workspace add` leaves `.jj/repo` as a file pointing at the main repo.
    withTempDir (fun parent ->
        let main = Path.Combine(parent, "main")
        let store = initFakeJjStore main

        gitStore store main [ "remote"; "add"; "origin"; "git@github.com:michaelglass/UnionConfig.git" ]
        |> ignore

        let workspace = Path.Combine(parent, "ws")
        writeFile workspace ".jj/repo" (Path.Combine(main, ".jj", "repo"))

        test <@ resolveOrigin workspace = originOf "https://github.com/michaelglass/UnionConfig" @>)

// -- checkRepositoryUrls --

[<Fact>]
let ``passes when RepositoryUrl names the origin repository`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "git@github.com:michaelglass/UnionConfig.git")
                (fsproj "<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>")

        test <@ (find repoCheck results).Outcome = Passed @>)

[<Fact>]
let ``fails when RepositoryUrl names a different repository, with the fix`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "git@github.com:michaelglass/UnionConfig.git")
                (fsproj "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        let result = find repoCheck results
        test <@ CheckOutcome.isFailed result.Outcome @>
        let r = reason result
        test <@ r.Contains "https://github.com/michaelglass/union-config" @>
        test <@ r.Contains "github.com/michaelglass/UnionConfig" @>
        test <@ r.Contains(Path.Combine("src", "MyPackage", "MyPackage.fsproj")) @>
        test <@ r.Contains "<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>" @>)

[<Fact>]
let ``skips with the reason when there is no origin`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (NoOrigin "no `origin` remote")
                (fsproj "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        let result = find repoCheck results
        test <@ result.Outcome = Skipped "no `origin` remote" @>)

[<Fact>]
let ``adds no check when RepositoryUrl is missing`` () =
    // "RepositoryUrl present" already fails such a project.
    withTempDir (fun dir ->
        let results =
            checkWith dir (originOf "https://github.com/michaelglass/UnionConfig") (fsproj "")

        test <@ List.isEmpty results @>)

[<Fact>]
let ``skips a RepositoryUrl that is not a hosted repository URL`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "https://github.com/michaelglass/UnionConfig")
                (fsproj "<RepositoryUrl>$(SomeProperty)</RepositoryUrl>")

        let result = find repoCheck results

        match result.Outcome with
        | Skipped r -> test <@ r.Contains "$(SomeProperty)" @>
        | other -> failwithf "expected a skip, got %A" other)

[<Fact>]
let ``reads RepositoryUrl from the nearest Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            (props "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        let results =
            checkWith dir (originOf "https://github.com/michaelglass/UnionConfig") (fsproj "")

        let result = find repoCheck results
        test <@ CheckOutcome.isFailed result.Outcome @>
        test <@ (reason result).Contains "Directory.Build.props" @>)

[<Fact>]
let ``passes on a matching RepositoryUrl from Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            (props "<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>")

        let results =
            checkWith dir (originOf "https://github.com/michaelglass/UnionConfig") (fsproj "")

        test <@ (find repoCheck results).Outcome = Passed @>)

[<Fact>]
let ``the fsproj RepositoryUrl overrides Directory.Build.props`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            (props "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        let results =
            checkWith
                dir
                (originOf "https://github.com/michaelglass/UnionConfig")
                (fsproj "<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>")

        test <@ (find repoCheck results).Outcome = Passed @>)

[<Fact>]
let ``only the nearest Directory.Build.props counts`` () =
    // MSBuild imports the nearest Directory.Build.props and stops there.
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            (props "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        writeFile dir "src/Directory.Build.props" (props "")

        let results =
            checkWith dir (originOf "https://github.com/michaelglass/UnionConfig") (fsproj "")

        test <@ List.isEmpty results @>)

[<Fact>]
let ``fails a github.com PackageProjectUrl for a different repository`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "https://github.com/michaelglass/UnionConfig")
                (fsproj
                    """<RepositoryUrl>https://github.com/michaelglass/UnionConfig</RepositoryUrl>
    <PackageProjectUrl>https://github.com/michaelglass/union-config</PackageProjectUrl>""")

        test <@ (find repoCheck results).Outcome = Passed @>
        let result = find projectUrlCheck results
        test <@ CheckOutcome.isFailed result.Outcome @>

        test
            <@
                (reason result).Contains
                    "<PackageProjectUrl>https://github.com/michaelglass/UnionConfig</PackageProjectUrl>"
            @>)

[<Fact>]
let ``passes a github.com PackageProjectUrl for the origin repository`` () =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "https://github.com/michaelglass/UnionConfig")
                (fsproj
                    "<PackageProjectUrl>https://github.com/michaelglass/UnionConfig/tree/main/docs</PackageProjectUrl>")

        test <@ (find projectUrlCheck results).Outcome = Passed @>)

[<Theory>]
[<InlineData("https://michaelglass.github.io/UnionConfig")>]
[<InlineData("$(RepositoryUrl)")>]
let ``adds no check for a PackageProjectUrl outside github.com`` (url: string) =
    withTempDir (fun dir ->
        let results =
            checkWith
                dir
                (originOf "https://github.com/michaelglass/UnionConfig")
                (fsproj (sprintf "<PackageProjectUrl>%s</PackageProjectUrl>" url))

        test <@ List.isEmpty results @>)

// -- runLint --
//
// Program.main output for this check lives in IntegrationTests: tests that
// change the current directory must share that test collection.

[<Fact>]
let ``runLint fails a packable project whose RepositoryUrl names another repository`` () =
    withTempDir (fun dir ->
        let store = initFakeJjStore dir

        gitStore store dir [ "remote"; "add"; "origin"; "git@github.com:michaelglass/UnionConfig.git" ]
        |> ignore

        writeFile
            dir
            "src/MyPackage/MyPackage.fsproj"
            (fsproj "<RepositoryUrl>https://github.com/michaelglass/union-config</RepositoryUrl>")

        writeFile
            dir
            "src/Tests/Tests.fsproj"
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>"

        let result = runLint dir

        let checksFor (name: string) =
            result.ProjectChecks |> List.find (fun (p, _) -> p.EndsWith name) |> snd

        test <@ CheckOutcome.isFailed (find repoCheck (checksFor "MyPackage.fsproj")).Outcome @>
        test <@ checksFor "Tests.fsproj" |> List.forall (fun c -> c.Name <> repoCheck) @>)

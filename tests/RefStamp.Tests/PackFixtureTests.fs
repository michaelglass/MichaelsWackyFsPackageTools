module RefStamp.Tests.PackFixtureTests

// End-to-end regression tests for src/RefStamp/build/RefStamp.targets — the
// MSBuild guard that makes a LOCAL `dotnet pack` version derive from the jj/git
// source ref, so choosing a release-shaped label is not an available action on
// a dev machine (AUTOMATION-123).
//
// Each test scaffolds a throwaway C# project in a temp dir (C#, not F#: an
// empty csproj compiles with zero package restores, so the packs stay fast and
// network-free), imports the REPO'S OWN RefStamp props/targets by absolute
// path — the same files the RefStamp package ships in build/ — and runs a real
// `dotnet pack`. The assertions are on the artifact NuGet actually produced
// (the .nupkg filename carries the package version), not on any intermediate.
//
// The pack-detection property `_IsPacking` is set by the `dotnet pack` CLI and
// is not a documented contract; these tests are the tripwire that catches an
// SDK bump renaming it (the failure mode would otherwise be SILENT: local
// packs quietly going back to release-shaped versions).
//
// jj-path tests follow the GitignoreLeakTests precedent: if jj is not
// installed (CI runners), the fixture cannot be built and the test is a no-op
// pass. The git-path and no-repo tests run everywhere.

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open Tests.Common.TestHelpers

// Pack tests shell out to a real `dotnet pack` (restore + compile + pack), so
// the repo-wide 5s/10s default test timeout (Tests.Common.Attributes) does not
// fit. Generous ceiling; typical packs complete in a few seconds.
[<Literal>]
let private PackTimeoutMs = 300_000

/// Repo root = nearest ancestor of the test assembly containing the solution
/// file. The fixtures import RefStamp's build/ files from here by absolute path.
let private repoRoot: string =
    let rec walk (dir: DirectoryInfo) =
        match dir with
        | null -> failwith "could not locate FSharpOssTooling.slnx above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "FSharpOssTooling.slnx")) -> d.FullName
        | d -> walk d.Parent

    walk (DirectoryInfo(AppContext.BaseDirectory))

let private refStampProps =
    Path.Combine(repoRoot, "src", "RefStamp", "build", "RefStamp.props")

let private refStampTargets =
    Path.Combine(repoRoot, "src", "RefStamp", "build", "RefStamp.targets")

/// The dotnet host that launched this test run (PATH's `dotnet` as fallback).
let private dotnetExe =
    match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
    | null
    | "" -> "dotnet"
    | path -> path

/// Run a process, returning (exitCode, stdout+stderr). `envOverrides` are
/// applied to the CHILD only: `Some v` sets, `None` removes — so "not CI" is a
/// property of the child pack process even when this test run IS in CI.
let private run
    (exe: string)
    (args: string list)
    (workDir: string)
    (envOverrides: (string * string option) list)
    : int * string =
    let psi = ProcessStartInfo(exe)
    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    for a in args do
        psi.ArgumentList.Add(a)

    for (key, value) in envOverrides do
        match value with
        | Some v -> psi.Environment.[key] <- v
        | None -> psi.Environment.Remove(key) |> ignore

    use p = Process.Start(psi)
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    p.ExitCode, stdout.Result + stderr.Result

/// Env for a pack that must look like a LOCAL dev pack: CI markers removed
/// (deterministically, even on a CI runner), MSBuild worker nodes disabled so
/// no build daemons outlive the test.
let private localPackEnv =
    [ "GITHUB_ACTIONS", None
      "CI", None
      "ReleaseBuild", None
      "MSBUILDDISABLENODEREUSE", Some "1"
      "DOTNET_NOLOGO", Some "1"
      "DOTNET_CLI_TELEMETRY_OPTOUT", Some "1" ]

let private writeFixtureProject (dir: string) =
    // Without this, the pack's own bin/obj output would dirty the fixture repo
    // BEFORE the ref probe runs (jj snapshots, git status) — every "clean"
    // fixture would read as dirty. Both jj and git honor .gitignore.
    File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n")

    File.WriteAllText(
        Path.Combine(dir, "Fixture.csproj"),
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="{refStampProps}" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>RefStampFixture</PackageId>
    <Version>1.2.3</Version>
    <Description>RefStamp test fixture</Description>
    <Authors>fixture</Authors>
  </PropertyGroup>
  <Import Project="{refStampTargets}" />
</Project>"""
    )

/// `dotnet pack` the fixture; returns (exitCode, output, versions of produced
/// .nupkg files — parsed from the artifact filenames NuGet wrote).
let private pack (dir: string) (extraArgs: string list) (env: (string * string option) list) =
    let code, output =
        run dotnetExe ([ "pack"; "-c"; "Release"; "--nologo" ] @ extraArgs) dir env

    let versions =
        let binRelease = Path.Combine(dir, "bin", "Release")

        if Directory.Exists binRelease then
            Directory.GetFiles(binRelease, "RefStampFixture.*.nupkg")
            |> Array.map (fun f ->
                let name = Path.GetFileName f
                name.Substring("RefStampFixture.".Length, name.Length - "RefStampFixture.".Length - ".nupkg".Length))
            |> Array.toList
        else
            []

    code, output, versions

// --- VCS fixture helpers ------------------------------------------------------

let private git (dir: string) (args: string list) =
    run
        "git"
        ([ "-c"
           "user.name=test"
           "-c"
           "user.email=test@example.com"
           "-c"
           "commit.gpgsign=false" ]
         @ args)
        dir
        []
    |> fst

/// jj with identity supplied via env, so the fixture works on unconfigured
/// machines (CI). Returns the exit code; -1 when jj cannot be launched at all.
let private jj (dir: string) (args: string list) : int =
    try
        run "jj" args dir [ "JJ_USER", Some "test"; "JJ_EMAIL", Some "test@example.com" ]
        |> fst
    with _ ->
        -1

let private jjAvailable: bool =
    lazy (jj (Path.GetTempPath()) [ "--version" ] = 0) |> fun l -> l.Value

/// jj repo with one described, non-empty working-copy commit — the everyday
/// "packing the change I just described" shape.
let private initDescribedJjRepo (dir: string) =
    jj dir [ "git"; "init" ] =! 0
    File.WriteAllText(Path.Combine(dir, "notes.txt"), "fixture content\n")
    jj dir [ "describe"; "-m"; "fixture work" ] =! 0

// --- version-shape oracles ----------------------------------------------------

// jj change ids use the reverse-hex alphabet k–z; commit/stash hashes are hex
// and carry a `g` marker so a hex-only segment can never read as a (leading-
// zero-invalid) numeric SemVer identifier.
let private jjCleanShape = Regex(@"^1\.2\.3-ref\.[k-z]{8}\.g[0-9a-f]{12}$")
let private jjDirtyShape = Regex(@"^1\.2\.3-ref\.[k-z]{8}\.g[0-9a-f]{12}\.dirty$")
let private gitCleanShape = Regex(@"^1\.2\.3-ref\.g[0-9a-f]{12}$")

let private gitDirtyShape =
    Regex(@"^1\.2\.3-ref\.g[0-9a-f]{12}\.dirty(\.g[0-9a-f]{12})?$")

// --- jj path ------------------------------------------------------------------

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``local pack in a jj repo derives the version from the described working copy`` () =
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir

            let code, output, versions = pack dir [] localPackEnv

            test <@ code = 0 @>
            test <@ output.Contains "RefStamp" @>

            match versions with
            | [ v ] -> test <@ jjCleanShape.IsMatch v @>
            | other -> failwith $"expected exactly one nupkg, got %A{other}")

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``local pack with an undescribed working copy is marked dirty`` () =
    if jjAvailable then
        withTempDir (fun dir ->
            jj dir [ "git"; "init" ] =! 0
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "undescribed work\n")
            writeFixtureProject dir

            let code, _, versions = pack dir [] localPackEnv

            test <@ code = 0 @>

            match versions with
            | [ v ] -> test <@ jjDirtyShape.IsMatch v @>
            | other -> failwith $"expected exactly one nupkg, got %A{other}")

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``local pack with an empty working copy uses the parent commit's ref`` () =
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir
            // Snapshot the fixture into the described commit, then park on a
            // fresh EMPTY working copy on top of it (the post-`jj new` shape).
            jj dir [ "describe"; "-m"; "fixture work with project" ] =! 0
            jj dir [ "new" ] =! 0

            let code, _, versions = pack dir [] localPackEnv

            test <@ code = 0 @>

            match versions with
            | [ v ] -> test <@ jjCleanShape.IsMatch v @>
            | other -> failwith $"expected exactly one nupkg, got %A{other}")

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``editing a source file between packs changes the version`` () =
    // The NuGet never-re-extract cache trap: same version string, different
    // bits. Killed by making the version track the TREE — an edit (snapshotted
    // by jj into a new commit id) must produce a different version.
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir
            jj dir [ "describe"; "-m"; "fixture work with project" ] =! 0

            let code1, _, versions1 = pack dir [] localPackEnv
            test <@ code1 = 0 @>

            File.AppendAllText(Path.Combine(dir, "notes.txt"), "edited between packs\n")

            let code2, _, versions2 = pack dir [] localPackEnv
            test <@ code2 = 0 @>

            match versions1, List.except versions1 versions2 with
            | [ v1 ], [ v2 ] ->
                test <@ v1 <> v2 @>
                test <@ jjCleanShape.IsMatch v1 && jjCleanShape.IsMatch v2 @>
            | other -> failwith $"expected one nupkg per pack, got %A{other}")

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``the built assembly's informational version carries the ref too`` () =
    // `fshw --version` (and any CommandTree CLI) reports the entry assembly's
    // informational version — so the ref must be stamped into the BINARY built
    // during the pack, not only into the nuspec.
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir

            let code, _, _ = pack dir [] localPackEnv
            test <@ code = 0 @>

            let assemblyInfo =
                Path.Combine(dir, "obj", "Release", "net10.0", "Fixture.AssemblyInfo.cs")
                |> File.ReadAllText

            let m =
                Regex.Match(assemblyInfo, "AssemblyInformationalVersion(?:Attribute)?\\(\"([^\"]+)\"\\)")

            test <@ m.Success @>
            let informational = m.Groups.[1].Value
            test <@ informational.Contains "-ref." @>)

// --- release paths (the ONLY ways to a clean version) -------------------------

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``the release pipeline's explicit flag produces the clean version`` () =
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir

            let code, _, versions = pack dir [ "-p:ReleaseBuild=true" ] localPackEnv

            test <@ code = 0 @>
            test <@ versions = [ "1.2.3" ] @>)

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``a CI environment produces the clean version`` () =
    if jjAvailable then
        withTempDir (fun dir ->
            initDescribedJjRepo dir
            writeFixtureProject dir

            let code, _, versions =
                pack dir [] ([ "GITHUB_ACTIONS", Some "true" ] @ localPackEnv |> List.distinctBy fst)

            test <@ code = 0 @>
            test <@ versions = [ "1.2.3" ] @>)

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``the release flag needs no repo at all`` () =
    // The release pipeline owns its clean semver (fssemantictagger); it must
    // not depend on ref detection working.
    withTempDir (fun dir ->
        writeFixtureProject dir

        let code, _, versions = pack dir [ "-p:ReleaseBuild=true" ] localPackEnv

        test <@ code = 0 @>
        test <@ versions = [ "1.2.3" ] @>)

// --- git fallback -------------------------------------------------------------

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``local pack in a plain git repo derives the version from HEAD`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q" ] =! 0
        writeFixtureProject dir
        git dir [ "add"; "." ] =! 0
        git dir [ "commit"; "-qm"; "fixture" ] =! 0

        let code, _, versions = pack dir [] localPackEnv

        test <@ code = 0 @>

        match versions with
        | [ v ] -> test <@ gitCleanShape.IsMatch v @>
        | other -> failwith $"expected exactly one nupkg, got %A{other}")

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``local pack in a dirty git repo is marked dirty`` () =
    withTempDir (fun dir ->
        git dir [ "init"; "-q" ] =! 0
        writeFixtureProject dir
        git dir [ "add"; "." ] =! 0
        git dir [ "commit"; "-qm"; "fixture" ] =! 0
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "uncommitted\n")
        git dir [ "add"; "notes.txt" ] =! 0

        let code, _, versions = pack dir [] localPackEnv

        test <@ code = 0 @>

        match versions with
        | [ v ] -> test <@ gitDirtyShape.IsMatch v @>
        | other -> failwith $"expected exactly one nupkg, got %A{other}")

// --- indeterminable ref -------------------------------------------------------

[<Xunit.Fact(Timeout = PackTimeoutMs)>]
let ``a pack whose ref cannot be determined fails loudly`` () =
    // NEVER fall back to the clean version: an untraceable pack is refused,
    // not silently release-shaped.
    withTempDir (fun dir ->
        writeFixtureProject dir

        let code, output, versions = pack dir [] localPackEnv

        test <@ code <> 0 @>
        test <@ List.isEmpty versions @>
        test <@ output.Contains "RefStamp" @>
        test <@ output.Contains "cannot determine" @>)

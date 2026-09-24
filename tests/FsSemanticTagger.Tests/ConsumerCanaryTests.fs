module FsSemanticTagger.Tests.ConsumerCanaryTests

open System
open System.IO
open Xunit
open Tests.Common
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.ConsumerCanary

let private configPath = "/home/someone/.fssemantictagger.json"

let private validJson =
    """
{
  "localFeed": "/feeds/local",
  "consumers": [
    { "package": "FsHotWatch.Cli", "repo": "/repos/intelligence",
      "pin": ".config/dotnet-tools.json", "gate": "./build.fsx check", "timeoutMinutes": 90 },
    { "package": "TestPrune.Core", "repo": "/repos/FsHotWatch",
      "pin": "src/FsHotWatch.TestPrune/FsHotWatch.TestPrune.fsproj", "gate": "mise run ci",
      "timeoutMinutes": 30, "revision": "main@origin" }
  ]
}
"""

let private pkg (name: string) : PackageConfig =
    { Name = name
      Fsproj = sprintf "src/%s/%s.fsproj" name name
      DllPath = sprintf "src/%s/bin/Release/net10.0/%s.dll" name name
      TagPrefix = name.ToLowerInvariant() + "-v"
      FsProjsSharingSameTag = [] }

let private v (s: string) : Version = parse s

// ---- config parsing ----

[<Fact>]
let ``parseConfig - reads every field and applies the defaults`` () =
    let config =
        match parseConfig configPath validJson with
        | Ok c -> c
        | Error e -> failwith e

    test <@ config.LocalFeed = "/feeds/local" @>
    test <@ config.Consumers.Length = 2 @>
    let first = config.Consumers[0]
    test <@ first.Package = "FsHotWatch.Cli" @>
    test <@ first.Repo = "/repos/intelligence" @>
    test <@ first.Pin = ".config/dotnet-tools.json" @>
    test <@ first.Gate = "./build.fsx check" @>
    test <@ first.Timeout = TimeSpan.FromMinutes 90.0 @>
    test <@ first.Revision = "main" @>
    test <@ config.Consumers[1].Revision = "main@origin" @>

[<Fact>]
let ``parseConfig - expands a leading tilde in repo and feed paths`` () =
    let json =
        """{ "localFeed": "~/feed", "consumers": [ { "package": "P", "repo": "~/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 1 } ] }"""

    let config =
        match parseConfig configPath json with
        | Ok c -> c
        | Error e -> failwith e

    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    test <@ config.LocalFeed = Path.Combine(home, "feed") @>
    test <@ config.Consumers[0].Repo = Path.Combine(home, "r") @>

[<Fact>]
let ``parseConfig - a missing field is an error naming the path and the field`` () =
    let json =
        """{ "consumers": [ { "package": "P", "repo": "/r", "gate": "true", "timeoutMinutes": 1 } ] }"""

    match parseConfig configPath json with
    | Ok _ -> failwith "expected an error"
    | Error msg ->
        test <@ msg.Contains configPath @>
        test <@ msg.Contains "pin" @>
        test <@ msg.Contains "consumers[0]" @>

[<Fact>]
let ``parseConfig - invalid JSON is an error naming the path`` () =
    match parseConfig configPath "{ not json" with
    | Ok _ -> failwith "expected an error"
    | Error msg -> test <@ msg.Contains configPath @>

[<Fact>]
let ``parseConfig - a non-positive timeout is an error naming the field`` () =
    let json =
        """{ "consumers": [ { "package": "P", "repo": "/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 0 } ] }"""

    match parseConfig configPath json with
    | Ok _ -> failwith "expected an error"
    | Error msg -> test <@ msg.Contains "timeoutMinutes" @>

[<Fact>]
let ``loadConfig - an absent file is NoConfig, not an error`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, ".fssemantictagger.json")
        test <@ loadConfig path = Ok(NoConfig path) @>)

[<Fact>]
let ``loadConfig - a present file is parsed`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, ".fssemantictagger.json")
        File.WriteAllText(path, validJson)

        match loadConfig path with
        | Ok(Loaded config) -> test <@ config.Consumers.Length = 2 @>
        | other -> failwithf "unexpected %A" other)

// ---- plan -> consumer selection ----

[<Fact>]
let ``selectConsumers - pairs each planned package with its consumers, case-insensitively, in plan order`` () =
    let config =
        match parseConfig configPath validJson with
        | Ok c -> c
        | Error e -> failwith e

    let plan =
        [ pkg "TestPrune.Core", v "12.0.0"
          pkg "fshotwatch.cli", v "0.15.0-alpha.1"
          pkg "TestPrune.Sql", v "1.0.0" ]

    let selected =
        selectConsumers config plan
        |> List.map (fun (p, version, c) -> p.Name, format version, c.Repo)

    let expected =
        [ "TestPrune.Core", "12.0.0", "/repos/FsHotWatch"
          "fshotwatch.cli", "0.15.0-alpha.1", "/repos/intelligence" ]

    test <@ selected = expected @>

// ---- pin editing ----

let private toolsManifest =
    """{
  "version": 1,
  "isRoot": true,
  "tools": {
    "fantomas": {
      "version": "7.0.5",
      "commands": [ "fantomas" ]
    },
    "fshotwatch.cli": {
      "commands": [
        "fshw"
      ],
      "version": "0.14.0-alpha.57",
      "rollForward": false
    }
  }
}
"""

[<Fact>]
let ``setPinVersion - dotnet-tools.json: only the matching tool's version changes`` () =
    let result =
        setPinVersion (ToolManifest ".config/dotnet-tools.json") "FsHotWatch.Cli" "0.14.0-alpha.58" toolsManifest

    let expected = toolsManifest.Replace("\"0.14.0-alpha.57\"", "\"0.14.0-alpha.58\"")
    test <@ result = Ok expected @>

[<Fact>]
let ``setPinVersion - dotnet-tools.json: an absent tool is an error naming the file and package`` () =
    match setPinVersion (ToolManifest ".config/dotnet-tools.json") "Nope" "1.0.0" toolsManifest with
    | Ok _ -> failwith "expected an error"
    | Error msg ->
        test <@ msg.Contains ".config/dotnet-tools.json" @>
        test <@ msg.Contains "Nope" @>

let private fsproj =
    """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="FSharp.Core" Version="10.1.*" />
    <!-- Keep in lockstep -->
    <PackageReference Include="TestPrune.Core" Version="11.0.0" />
    <PackageReference Include="TestPrune.Core.Extras" Version="11.0.0" />
  </ItemGroup>
</Project>
"""

[<Fact>]
let ``setPinVersion - fsproj: only the exact PackageReference changes, byte-exact otherwise`` () =
    let result =
        setPinVersion (MsBuildProject "a.fsproj") "testprune.core" "12.0.0" fsproj

    let expected =
        fsproj.Replace(
            """<PackageReference Include="TestPrune.Core" Version="11.0.0" />""",
            """<PackageReference Include="TestPrune.Core" Version="12.0.0" />"""
        )

    test <@ result = Ok expected @>
    test <@ (expected.Contains """Include="TestPrune.Core.Extras" Version="11.0.0" """) @>

[<Fact>]
let ``setPinVersion - fsproj: every reference to the package is bumped`` () =
    let twice =
        fsproj.Replace(
            "</ItemGroup>",
            """  <PackageReference Include="TestPrune.Core" Version="11.0.0" />
  </ItemGroup>"""
        )

    match setPinVersion (MsBuildProject "a.fsproj") "TestPrune.Core" "12.0.0" twice with
    | Ok edited ->
        test <@ not (edited.Contains """Include="TestPrune.Core" Version="11.0.0" """) @>
        test <@ edited.Contains """Include="TestPrune.Core.Extras" Version="11.0.0" """ @>
    | Error e -> failwith e

[<Fact>]
let ``pinFileOf - the file name decides the shape`` () =
    test <@ pinFileOf ".config/dotnet-tools.json" = Ok(ToolManifest ".config/dotnet-tools.json") @>
    test <@ pinFileOf "src/A/A.fsproj" = Ok(MsBuildProject "src/A/A.fsproj") @>
    test <@ pinFileOf "Directory.Packages.props" = Ok(MsBuildProject "Directory.Packages.props") @>

    match pinFileOf "paket.dependencies" with
    | Ok _ -> failwith "expected an error"
    | Error msg -> test <@ msg.Contains "paket.dependencies" @>

// ---- nuget.config ----

[<Fact>]
let ``withLocalFeed - no existing config writes a minimal one`` () =
    let content = withLocalFeed None "/feeds/local"
    test <@ content.Contains """<add key="fssemantictagger-consumer-canary" value="/feeds/local" />""" @>
    test <@ content.Contains "<packageSources>" @>

[<Fact>]
let ``withLocalFeed - an existing config gains the feed after any clear and keeps the rest`` () =
    let existing =
        """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"""

    let content = withLocalFeed (Some existing) "/feeds/local"
    let clearAt = content.IndexOf "<clear />"
    let feedAt = content.IndexOf "fssemantictagger-consumer-canary"
    test <@ clearAt >= 0 && feedAt > clearAt @>
    test <@ content.Contains """<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />""" @>
    test <@ content.IndexOf "</packageSources>" > feedAt @>

// ---- orchestration through a fake host ----

/// Copy a consumer checkout's files into a workspace path, as `git worktree add`
/// would populate it.
let private copyTree (source: string) (target: string) =
    for file in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(source, file)
        let destination = Path.Combine(target, relative)
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        File.Copy(file, destination)

/// Records every command; `dotnet` succeeds without running, a `git worktree
/// add` populates the workspace from the checkout, and the gate is answered by
/// `gate`.
let private fakeOps (commands: ResizeArray<string>) (gate: string -> GateOutcome) : Ops =
    { RunIn =
        fun cwd cmd args ->
            commands.Add(sprintf "%s: %s %s" cwd cmd args)

            if cmd = "git" && args.StartsWith "worktree add --detach " then
                copyTree cwd (args.Split(' ')[3])

            if cmd = "jj" && args.StartsWith "workspace add " then
                copyTree cwd (Array.last (args.Split ' '))

            Success ""
      RunGate =
        fun cwd command _timeout logPath ->
            commands.Add(sprintf "%s: sh -c %s -> %s" cwd command logPath)
            File.AppendAllText(logPath, command + "\n")
            gate command }

/// A consumer repo with a `.git` marker and a PackageReference pin, and a
/// root dir with a feed and log dir beside it.
let private scaffold (dir: string) (gate: string -> GateOutcome) (commands: ResizeArray<string>) =
    let repo = Path.Combine(dir, "consumer")
    Directory.CreateDirectory(Path.Combine(repo, ".git")) |> ignore
    Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
    File.WriteAllText(Path.Combine(repo, "src", "C.fsproj"), fsproj)
    let root = Path.Combine(dir, "producer")
    Directory.CreateDirectory root |> ignore

    let settings =
        { ConfigPath = Path.Combine(dir, ".fssemantictagger.json")
          Skip = false
          LogDir = Path.Combine(root, "artifacts", "consumer-canary")
          PackagesCache = Path.Combine(dir, "cache")
          Ops = fakeOps commands gate }

    let config =
        { LocalFeed = Path.Combine(dir, "feed")
          Consumers =
            [ { Package = "TestPrune.Core"
                Repo = repo
                Pin = "src/C.fsproj"
                Gate = "mise run ci"
                Timeout = TimeSpan.FromMinutes 1.0
                Revision = "main" } ] }

    root, repo, settings, config

[<Fact>]
let ``run - green gate passes and the workspace saw the feed, the pin and the log`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        let stale = Path.Combine(settings.PackagesCache, "testprune.core", "12.0.0")
        Directory.CreateDirectory stale |> ignore

        let result = run settings root config [ pkg "TestPrune.Core", v "12.0.0" ]

        match result with
        | Ok passed -> test <@ passed |> List.map (fun (p, _, _) -> p.Name) = [ "TestPrune.Core" ] @>
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        // The candidate at its planned version was packed into the feed and its cache entry evicted.
        test
            <@
                commands
                |> Seq.exists (fun c ->
                    c.StartsWith(root + ": dotnet pack src/TestPrune.Core/TestPrune.Core.fsproj")
                    && c.Contains "-p:Version=12.0.0"
                    && c.Contains "-p:ReleaseBuild=true"
                    && c.Contains config.LocalFeed)
            @>

        test <@ not (Directory.Exists stale) @>
        // A git consumer gets a detached worktree off its revision, beside the repo.
        let workspace = repo + "-canary-testprune-core"

        test
            <@
                commands
                |> Seq.exists (fun c -> c = sprintf "%s: git worktree add --detach %s main" repo workspace)
            @>)

[<Fact>]
let ``run - edits the pin, writes nuget.config, restores, then runs the gate in the workspace`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let pinned = File.ReadAllText(Path.Combine(workspace, "src", "C.fsproj"))
        test <@ pinned.Contains """<PackageReference Include="TestPrune.Core" Version="12.0.0" />""" @>
        let nugetConfig = File.ReadAllText(Path.Combine(workspace, "nuget.config"))
        test <@ nugetConfig.Contains config.LocalFeed @>

        let logged =
            commands
            |> Seq.filter (fun c -> c.StartsWith(workspace + ": sh -c"))
            |> List.ofSeq

        test <@ logged.Length = 2 @>
        test <@ logged[0].Contains "dotnet restore src/C.fsproj" @>
        test <@ logged[1].Contains "mise run ci" @>
        let logPath = Path.Combine(settings.LogDir, "TestPrune.Core-12.0.0-consumer.log")
        test <@ File.Exists logPath @>
        test <@ (File.ReadAllText logPath).Contains "mise run ci" @>)

[<Fact>]
let ``run - a red gate refuses with the consumer, exit code, log and workspace`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let root, repo, settings, config =
            scaffold dir (fun c -> if c.StartsWith "dotnet restore" then Exited 0 else Exited 3) commands

        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> failwith "expected a refusal"
        | Error refusal ->
            test <@ refusal.Reason = GateFailed 3 @>
            let message = formatRefusal refusal
            test <@ message.Contains "REFUSED" @>
            test <@ message.Contains repo @>
            test <@ message.Contains "TestPrune.Core 12.0.0" @>
            test <@ message.Contains "exited 3" @>
            test <@ message.Contains "TestPrune.Core-12.0.0-consumer.log" @>
            test <@ message.Contains workspace @>
            test <@ message.Contains "--skip-consumer-canary" @>
            test <@ Directory.Exists workspace @>)

[<Fact>]
let ``run - a gate that exceeds its timeout refuses naming the budget`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let root, repo, settings, config =
            scaffold
                dir
                (fun c ->
                    if c.StartsWith "dotnet restore" then
                        Exited 0
                    else
                        TimedOut(TimeSpan.FromMinutes 1.0))
                commands

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> failwith "expected a refusal"
        | Error refusal ->
            test <@ refusal.Reason = GateTimedOut(TimeSpan.FromMinutes 1.0) @>
            test <@ (formatRefusal refusal).Contains "1m0s" @>)

[<Fact>]
let ``run - a failed restore refuses before the gate runs`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let root, repo, settings, config =
            scaffold dir (fun c -> if c.StartsWith "dotnet restore" then Exited 1 else Exited 0) commands

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> failwith "expected a refusal"
        | Error refusal ->
            test <@ refusal.Reason = RestoreFailed 1 @>
            test <@ not (commands |> Seq.exists (fun c -> c.Contains "mise run ci")) @>)

[<Fact>]
let ``run - a plan with no consumers runs nothing`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, config = scaffold dir (fun _ -> Exited 0) commands
        test <@ run settings root config [ pkg "Other", v "1.0.0" ] = Ok [] @>
        test <@ commands.Count = 0 @>)

// ---- the decision the release makes ----

[<Fact>]
let ``decide - absent config skips with a note naming the path`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, _ = scaffold dir (fun _ -> Exited 0) commands

        let output, verdict =
            withCapturedConsole (fun () -> decide settings root [ pkg "TestPrune.Core", v "12.0.0" ])

        test <@ verdict = Ok(Skipped(NoConsumerConfig settings.ConfigPath)) @>
        test <@ output.Contains settings.ConfigPath @>)

[<Fact>]
let ``decide - break-glass skips loudly and is recorded`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, config = scaffold dir (fun _ -> Exited 3) commands
        File.WriteAllText(settings.ConfigPath, toJson config)
        let settings = { settings with Skip = true }

        let output, verdict =
            withCapturedConsole (fun () -> decide settings root [ pkg "TestPrune.Core", v "12.0.0" ])

        test <@ verdict = Ok(Skipped BreakGlass) @>
        test <@ output.Contains "--skip-consumer-canary" @>
        test <@ (formatVerdict (Skipped BreakGlass)).Contains "--skip-consumer-canary" @>
        test <@ commands.Count = 0 @>)

[<Fact>]
let ``decide - a malformed config refuses`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, _ = scaffold dir (fun _ -> Exited 0) commands
        File.WriteAllText(settings.ConfigPath, "{ nope")

        match decide settings root [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error msg -> test <@ msg.Contains settings.ConfigPath @>
        | Ok _ -> failwith "expected a refusal")

[<Fact>]
let ``describePlan - lists the consumers a dry run would exercise`` () =
    let config =
        match parseConfig configPath validJson with
        | Ok c -> c
        | Error e -> failwith e

    let lines =
        describePlan config [ pkg "TestPrune.Core", v "12.0.0"; pkg "Other", v "1.0.0" ]

    test <@ lines.Length = 1 @>
    test <@ lines[0].Contains "TestPrune.Core 12.0.0" @>
    test <@ lines[0].Contains "/repos/FsHotWatch" @>
    test <@ lines[0].Contains "mise run ci" @>

// ---- integration: a real git consumer and a fake gate script ----

[<Literal>]
let private IntegrationTimeoutMs = 120_000

/// Real workspace creation and a real shell gate; only `dotnet` is answered
/// without running, since the fixture has nothing to pack or restore.
let private integrationOps: Ops =
    { RunIn =
        fun cwd cmd args ->
            if cmd = "dotnet" then
                Success ""
            else
                Shell.runIn cwd cmd args
      RunGate =
        fun cwd command timeout logPath ->
            if command.StartsWith "dotnet " then
                File.AppendAllText(logPath, command + "\n")
                Exited 0
            else
                Shell.runLogged cwd command timeout logPath }

/// A committed git consumer at `<dir>/consumer`; a repeat call returns the
/// existing one so a second canary run sees the leftover worktree.
let private gitRepoWithGate (dir: string) (gateExit: int) : string =
    let repo = Path.Combine(dir, "consumer")

    if not (Directory.Exists(Path.Combine(repo, ".git"))) then
        Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
        File.WriteAllText(Path.Combine(repo, "src", "C.fsproj"), fsproj)

        File.WriteAllText(
            Path.Combine(repo, "gate.sh"),
            sprintf "#!/bin/sh\necho gate ran in $(pwd)\nexit %d\n" gateExit
        )

        for args in
            [ "init -q -b main"
              "-c user.email=t@example.com -c user.name=t add ."
              "-c user.email=t@example.com -c user.name=t commit -q -m init" ] do
            Shell.runOrFail "git" (sprintf "-C %s %s" repo args) |> ignore

    repo

let private integrationRun (dir: string) (gateExit: int) =
    let repo = gitRepoWithGate dir gateExit
    let root = Path.Combine(dir, "producer")
    Directory.CreateDirectory root |> ignore

    let settings =
        { ConfigPath = Path.Combine(dir, ".fssemantictagger.json")
          Skip = false
          LogDir = Path.Combine(root, "artifacts", "consumer-canary")
          PackagesCache = Path.Combine(dir, "cache")
          Ops = integrationOps }

    let config =
        { LocalFeed = Path.Combine(dir, "feed")
          Consumers =
            [ { Package = "TestPrune.Core"
                Repo = repo
                Pin = "src/C.fsproj"
                Gate = "sh gate.sh"
                Timeout = TimeSpan.FromMinutes 1.0
                Revision = "main" } ] }

    repo, settings, run settings root config [ pkg "TestPrune.Core", v "12.0.0" ]

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``integration - a git consumer's green gate runs in a fresh worktree with the pin bumped`` () =
    withTempDir (fun dir ->
        let repo, settings, result = integrationRun dir 0

        match result with
        | Ok passed -> test <@ passed.Length = 1 @>
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let workspace = repo + "-canary-testprune-core"

        let log =
            File.ReadAllText(Path.Combine(settings.LogDir, "TestPrune.Core-12.0.0-consumer.log"))

        test <@ log.Contains "gate ran in " && log.Contains(Path.GetFileName workspace) @>
        test <@ (File.ReadAllText(Path.Combine(workspace, "src", "C.fsproj"))).Contains "12.0.0" @>
        // The consumer's own tree is untouched.
        test <@ (File.ReadAllText(Path.Combine(repo, "src", "C.fsproj"))).Contains "11.0.0" @>
        // A second run replaces the leftover worktree instead of failing on it.
        let _, _, again = integrationRun dir 0
        test <@ Result.isOk again @>)

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``integration - a git consumer's red gate refuses and keeps the worktree`` () =
    withTempDir (fun dir ->
        let repo, _, result = integrationRun dir 1

        match result with
        | Ok _ -> failwith "expected a refusal"
        | Error refusal ->
            test <@ refusal.Reason = GateFailed 1 @>
            test <@ Directory.Exists(repo + "-canary-testprune-core") @>)

// ---- the release honours the verdict ----

/// A single-package jj repo at `dir` whose release commit is pushed with green
/// CI, driven by `canary`. Returns the exit code, the console output, the
/// commands the release issued, and the fsproj path.
let private releaseWithVersion (fsprojVersion: string) (dir: string) (canary: Settings) (mode: Release.ReleaseMode) =
    let fsproj = Path.Combine(dir, "MyLib.fsproj")

    File.WriteAllText(
        fsproj,
        sprintf
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>%s</Version></PropertyGroup>
</Project>"""
            fsprojVersion
    )

    File.WriteAllText(Path.Combine(dir, "CHANGELOG.md"), "# Changelog\n\n## Unreleased\n\n- feat: something\n")
    let calls = ResizeArray<string * string>()

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls.Add((cmd, args))

        match cmd, args with
        | "jj", "diff --summary" -> Success ""
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "parent1"
        | "jj", a when a.Contains "remote_bookmarks()" -> Success "parent1"
        | "gh", a when a.Contains "run list" ->
            Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        // Tags: v0.1.0-alpha.1 exists when the fsproj is already bumped past it.
        | "jj", a when a.Contains "tag list" && a.Contains "\"glob:v" ->
            Success(if fsprojVersion = "0.0.0" then "" else "v0.1.0-alpha.1")
        | "jj", a when a.Contains "--from v0.1.0-alpha.1" -> Success "1 file changed"
        | "jj", "tag list v0.2.0-alpha.1" -> Success ""
        | "git", a when a.StartsWith "tag -l" -> Success ""
        | "jj", a when a.StartsWith "tag set" -> Success ""
        | "jj", a when a.StartsWith "commit" -> Success ""
        | "jj", a when a.StartsWith "bookmark set" -> Success ""
        | "jj", "git push" -> Success ""
        | "jj", "git export" -> Success ""
        | "git", a when a.StartsWith "push origin" -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args, 1)

    let config: ToolConfig =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = fsproj
                DllPath = "bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = []
          PublishWorkflows = defaultPublishWorkflows
          RootDir = dir }

    let output, code =
        withCapturedConsole (fun () ->
            Release.release
                { Run = fakeRun
                  Config = config
                  Command = Release.StartAlpha
                  Mode = mode
                  TargetPackages = []
                  ExtractPreviousApi = fun _ _ -> Api.FetchError "none"
                  ExtractCurrentApi = fun _ -> []
                  ExtractPreviousGrammar = fun _ _ -> None
                  ExtractCurrentGrammar = fun _ -> None
                  CiPollIntervalMs = 0
                  CiMaxAttempts = 10
                  TagPush =
                    { PushAttempts = 1
                      PushRetryDelayMs = 0
                      RunPollIntervalMs = 0
                      RunPollAttempts = 1 }
                  CheckFeedPresence = fun _ _ -> Api.OnFeed
                  CheckRestorable = fun _ _ _ -> Api.OnFeed
                  WaitForNuGet = false
                  NuGetPollIntervalMs = 0
                  NuGetMaxAttempts = 1
                  Push = false
                  Check = false
                  Canary = canary })

    code, output, List.ofSeq calls, fsproj

let private releaseWith (dir: string) (canary: Settings) (mode: Release.ReleaseMode) =
    releaseWithVersion "0.0.0" dir canary mode

/// Settings whose config names one consumer of MyLib, gated by `gate`.
let private canaryFor (dir: string) (gate: string -> GateOutcome) (commands: ResizeArray<string>) : Settings =
    let repo = Path.Combine(dir, "consumer")
    Directory.CreateDirectory(Path.Combine(repo, ".git")) |> ignore
    Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
    File.WriteAllText(Path.Combine(repo, "src", "C.fsproj"), fsproj.Replace("TestPrune.Core", "MyLib"))

    let config =
        { LocalFeed = Path.Combine(dir, "feed")
          Consumers =
            [ { Package = "MyLib"
                Repo = repo
                Pin = "src/C.fsproj"
                Gate = "mise run ci"
                Timeout = TimeSpan.FromMinutes 1.0
                Revision = "main" } ] }

    let configPath = Path.Combine(dir, ".fssemantictagger.json")
    File.WriteAllText(configPath, toJson config)

    { ConfigPath = configPath
      Skip = false
      LogDir = Path.Combine(dir, "logs")
      PackagesCache = Path.Combine(dir, "cache")
      Ops = fakeOps commands gate }

[<Fact>]
let ``release - a red consumer gate refuses before any write, tag or push`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let canary =
            canaryFor dir (fun c -> if c.StartsWith "dotnet restore" then Exited 0 else Exited 2) commands

        let code, output, calls, fsprojPath = releaseWith dir canary Release.PushTags
        test <@ code = 1 @>
        test <@ output.Contains "Consumer canary REFUSED the release" @>
        test <@ output.Contains "exited 2" @>
        test <@ (File.ReadAllText fsprojPath).Contains "<Version>0.0.0</Version>" @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith "tag set")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith "commit")) @>)

[<Fact>]
let ``release - a green consumer gate lets the tag through and the pack named the planned version`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let canary = canaryFor dir (fun _ -> Exited 0) commands
        let code, output, calls, _ = releaseWith dir canary Release.PushTags
        test <@ code = 0 @>
        test <@ output.Contains "Consumer canary: passed" @>
        test <@ commands |> Seq.exists (fun c -> c.Contains "-p:Version=0.1.0-alpha.1") @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains "tag set --allow-move v0.1.0-alpha.1")
            @>)

[<Fact>]
let ``release - break-glass skips the canary loudly and the output records it`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let canary =
            { canaryFor dir (fun _ -> Exited 2) commands with
                Skip = true }

        let code, output, calls, _ = releaseWith dir canary Release.PushTags
        test <@ code = 0 @>
        test <@ output.Contains "CONSUMER CANARY SKIPPED: --skip-consumer-canary" @>
        test <@ output.Contains "Consumer canary: SKIPPED by --skip-consumer-canary" @>
        test <@ commands.Count = 0 @>
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith "tag set") @>)

[<Fact>]
let ``release - dry run lists the consumers that would run and runs none`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let canary = canaryFor dir (fun _ -> Exited 2) commands
        let code, output, _, _ = releaseWith dir canary Release.DryRun
        test <@ code = 0 @>
        test <@ output.Contains "Consumer canary would run:" @>
        test <@ output.Contains "MyLib 0.1.0-alpha.1" @>
        test <@ output.Contains "mise run ci" @>
        test <@ commands.Count = 0 @>)

[<Fact>]
let ``release - no config on the machine skips with a note naming the path`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let canary = canaryFor dir (fun _ -> Exited 2) commands
        File.Delete canary.ConfigPath
        let code, output, _, _ = releaseWith dir canary Release.PushTags
        test <@ code = 0 @>
        test <@ output.Contains(sprintf "Consumer canary: skipped (no %s on this machine)" canary.ConfigPath) @>
        test <@ commands.Count = 0 @>)

[<Fact>]
let ``release - resuming an already-bumped release still runs the canary before tagging`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let canary =
            canaryFor dir (fun c -> if c.StartsWith "dotnet restore" then Exited 0 else Exited 4) commands

        let code, output, calls, _ =
            releaseWithVersion "0.2.0-alpha.1" dir canary Release.PushTags

        test <@ code = 1 @>
        test <@ output.Contains "Resuming in-progress release" @>
        test <@ output.Contains "exited 4" @>
        test <@ commands |> Seq.exists (fun c -> c.Contains "-p:Version=0.2.0-alpha.1") @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith "tag set")) @>)

[<Fact>]
let ``release - dry run with break-glass says so and a dry run with no consumer of the plan says that`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let canary =
            { canaryFor dir (fun _ -> Exited 2) commands with
                Skip = true }

        let _, output, _, _ = releaseWith dir canary Release.DryRun
        test <@ output.Contains "SKIPPED by --skip-consumer-canary" @>

        let unrelated =
            { LocalFeed = Path.Combine(dir, "feed")
              Consumers =
                [ { Package = "Other"
                    Repo = dir
                    Pin = "x.fsproj"
                    Gate = "true"
                    Timeout = TimeSpan.FromMinutes 1.0
                    Revision = "main" } ] }

        File.WriteAllText(canary.ConfigPath, toJson unrelated)
        let _, output, _, _ = releaseWith dir { canary with Skip = false } Release.DryRun
        test <@ output.Contains "names no consumer of these packages" @>

        File.WriteAllText(canary.ConfigPath, "{ nope")
        let code, output, _, _ = releaseWith dir { canary with Skip = false } Release.DryRun
        test <@ code = 0 @>
        test <@ output.Contains "Warning:" && output.Contains canary.ConfigPath @>)

[<Fact>]
let ``release - a config naming no consumer of the plan skips with a note`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let canary = canaryFor dir (fun _ -> Exited 2) commands

        let unrelated =
            { LocalFeed = Path.Combine(dir, "feed")
              Consumers =
                [ { Package = "Other"
                    Repo = dir
                    Pin = "x.fsproj"
                    Gate = "true"
                    Timeout = TimeSpan.FromMinutes 1.0
                    Revision = "main" } ] }

        File.WriteAllText(canary.ConfigPath, toJson unrelated)
        let code, output, _, _ = releaseWith dir canary Release.PushTags
        test <@ code = 0 @>
        test <@ output.Contains "names no consumer of these packages" @>
        test <@ commands.Count = 0 @>)

// ---- the remaining edges ----

[<Fact>]
let ``parseConfig - consumers must be an array and fields must be strings`` () =
    let notArray = """{ "consumers": {} }"""

    match parseConfig configPath notArray with
    | Error msg -> test <@ msg.Contains "\"consumers\" must be an array" @>
    | Ok _ -> failwith "expected an error"

    let notString =
        """{ "consumers": [ { "package": 1, "repo": "/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 1 } ] }"""

    match parseConfig configPath notString with
    | Error msg -> test <@ msg.Contains "consumers[0].package must be a string" @>
    | Ok _ -> failwith "expected an error"

    let badRevision =
        """{ "consumers": [ { "package": "P", "repo": "/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 1, "revision": 2 } ] }"""

    match parseConfig configPath badRevision with
    | Error msg -> test <@ msg.Contains "\"revision\" must be a string" @>
    | Ok _ -> failwith "expected an error"

    match parseConfig configPath """{ "localFeed": "/f" }""" with
    | Error msg -> test <@ msg.Contains "\"consumers\"" @>
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``setPinVersion - fsproj: an absent reference is an error naming the file and package`` () =
    match setPinVersion (MsBuildProject "a.fsproj") "Nope" "1.0.0" fsproj with
    | Error msg -> test <@ msg.Contains "a.fsproj" && msg.Contains "Nope" @>
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``withLocalFeed - a config without packageSources gains the section`` () =
    let inside =
        withLocalFeed (Some "<configuration>\n</configuration>\n") "/feeds/local"

    test
        <@
            inside.Contains "<packageSources>"
            && inside.IndexOf "</configuration>" > inside.IndexOf "/feeds/local"
        @>

    let bare = withLocalFeed (Some "<!-- empty -->") "/feeds/local"
    test <@ bare.StartsWith "<!-- empty -->" && bare.Contains "/feeds/local" @>

[<Fact>]
let ``formatRefusal - every reason reads as a sentence with the consumer and package`` () =
    let refusal reason =
        { Package = pkg "P"
          Version = v "1.0.0"
          Consumer =
            { Package = "P"
              Repo = "/repos/c"
              Pin = "p.fsproj"
              Gate = "mise run ci"
              Timeout = TimeSpan.FromMinutes 1.0
              Revision = "main" }
          Reason = reason
          LogPath = None
          Workspace = None }

    let expectations =
        [ PackFailed "boom", "packing the candidate failed: boom"
          WorkspaceFailed "no", "creating the workspace failed: no"
          PinFailed "bad", "pinning the candidate failed: bad"
          RestoreFailed 2, "restoring the candidate exited 2"
          RestoreTimedOut(TimeSpan.FromSeconds 90.0), "restoring the candidate exceeded its 1m30s budget"
          GateFailed 3, "gate `mise run ci` exited 3"
          GateTimedOut(TimeSpan.FromMinutes 2.0), "gate `mise run ci` exceeded its 2m0s budget" ]

    for (reason, phrase) in expectations do
        let text = formatRefusal (refusal reason)
        test <@ text.Contains phrase && text.Contains "/repos/c (P 1.0.0)" @>
        test <@ not (text.Contains "log:") && not (text.Contains "workspace:") @>

[<Fact>]
let ``formatVerdict - the skip and empty verdicts name what happened`` () =
    test <@ (formatVerdict (Skipped(NoConsumersForPlan "/c.json"))).Contains "/c.json names no consumer" @>
    test <@ formatVerdict (Passed []) = "Consumer canary: nothing to run" @>

[<Fact>]
let ``defaultSettings - reads the home config and logs under the releasing repo`` () =
    let settings = defaultSettings true "/repo"
    test <@ settings.Skip @>
    test <@ Path.GetFileName settings.ConfigPath = ".fssemantictagger.json" @>
    test <@ settings.LogDir = Path.Combine("/repo", "artifacts", "consumer-canary") @>
    test <@ settings.PackagesCache <> "" @>

[<Fact>]
let ``run - a jj consumer gets a jj workspace, replacing a leftover`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        Directory.Delete(Path.Combine(repo, ".git"))
        Directory.CreateDirectory(Path.Combine(repo, ".jj")) |> ignore
        let workspace = repo + "-canary-testprune-core"
        Directory.CreateDirectory workspace |> ignore
        File.WriteAllText(Path.Combine(workspace, "leftover.txt"), "old")

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        test
            <@
                commands
                |> Seq.exists (fun c -> c = sprintf "%s: jj workspace forget canary-testprune-core" repo)
            @>

        test
            <@
                commands
                |> Seq.exists (fun c ->
                    c = sprintf "%s: jj workspace add --name canary-testprune-core -r main %s" repo workspace)
            @>

        test <@ not (File.Exists(Path.Combine(workspace, "leftover.txt"))) @>)

[<Fact>]
let ``run - a consumer that is neither jj nor git, or does not exist, refuses`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        Directory.Delete(Path.Combine(repo, ".git"))

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = WorkspaceFailed msg } -> test <@ msg.Contains "neither a jj nor a git repository" @>
        | other -> failwithf "unexpected %A" other

        let missing =
            { config with
                Consumers =
                    [ { config.Consumers[0] with
                          Repo = Path.Combine(dir, "nowhere") } ] }

        match run settings root missing [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = WorkspaceFailed msg } -> test <@ msg.Contains "does not exist" @>
        | other -> failwithf "unexpected %A" other)

[<Fact>]
let ``run - a failed pack refuses before any workspace is made`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands

        let settings =
            { settings with
                Ops =
                    { settings.Ops with
                        RunIn =
                            fun cwd cmd args ->
                                if cmd = "dotnet" then
                                    Failure("NU1", 7)
                                else
                                    settings.Ops.RunIn cwd cmd args } }

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error refusal ->
            let expected =
                PackFailed(
                    "dotnet pack src/TestPrune.Core/TestPrune.Core.fsproj -c Release -p:ReleaseBuild=true -p:Version=12.0.0 -o "
                    + config.LocalFeed
                    + " exited 7: NU1"
                )

            test <@ refusal.Reason = expected @>
            test <@ refusal.LogPath = None @>
            test <@ not (Directory.Exists(repo + "-canary-testprune-core")) @>
        | Ok _ -> failwith "expected a refusal")

[<Fact>]
let ``run - a pin missing from the workspace, a restore timeout, and an existing nuget.config`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let root, repo, settings, config =
            scaffold
                dir
                (fun c ->
                    if c.StartsWith "dotnet tool restore" then
                        TimedOut(TimeSpan.FromMinutes 1.0)
                    else
                        Exited 0)
                commands

        let absent =
            { config with
                Consumers =
                    [ { config.Consumers[0] with
                          Pin = "src/Missing.fsproj" } ] }

        match run settings root absent [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = PinFailed msg } -> test <@ msg.Contains "src/Missing.fsproj" @>
        | other -> failwithf "unexpected %A" other

        File.WriteAllText(
            Path.Combine(repo, "NuGet.Config"),
            "<configuration>\n  <packageSources>\n    <clear />\n  </packageSources>\n</configuration>\n"
        )

        File.WriteAllText(
            Path.Combine(repo, "dotnet-tools.json"),
            toolsManifest.Replace("fshotwatch.cli", "testprune.core")
        )

        let manifest =
            { config with
                Consumers =
                    [ { config.Consumers[0] with
                          Pin = "dotnet-tools.json" } ] }

        match run settings root manifest [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = RestoreTimedOut _ } -> ()
        | other -> failwithf "unexpected %A" other

        let workspace = repo + "-canary-testprune-core"

        test
            <@
                commands
                |> Seq.exists (fun c -> c.Contains "dotnet tool restore --tool-manifest dotnet-tools.json")
            @>

        let merged = File.ReadAllText(Path.Combine(workspace, "NuGet.Config"))

        test
            <@
                merged.Contains "<clear />"
                && merged.IndexOf config.LocalFeed > merged.IndexOf "<clear />"
            @>

        test <@ (File.ReadAllText(Path.Combine(workspace, "dotnet-tools.json"))).Contains "\"12.0.0\"" @>)

// ---- Shell ----

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``runIn - a failure with nothing on stderr reports stdout`` () =
    withTempDir (fun dir ->
        match Shell.runIn dir "sh" "-c \"echo out; exit 3\"" with
        | Failure(msg, 3) -> test <@ msg = "out" @>
        | other -> failwithf "unexpected %A" other

        test
            <@
                Shell.runIn dir "sh" "-c pwd"
                |> (function
                | Success p -> p.EndsWith(Path.GetFileName dir)
                | _ -> false)
            @>)

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``runLogged - a command past its budget is killed and the log says so`` () =
    withTempDir (fun dir ->
        let log = Path.Combine(dir, "logs", "gate.log")

        let outcome =
            Shell.runLogged dir "echo started; sleep 30; echo never" (TimeSpan.FromSeconds 1.0) log

        test <@ outcome = TimedOut(TimeSpan.FromSeconds 1.0) @>
        let text = File.ReadAllText log
        test <@ text.Contains "started" @>
        test <@ text.Contains "killed after 0m1s" @>
        test <@ not (text.Contains "\nnever") @>)

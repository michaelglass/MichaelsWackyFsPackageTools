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
open FsSemanticTagger.Tests.ExtractionFakes

let private configPath = "/home/someone/.fssemantictagger.json"

let private validJson =
    """
{
  "localFeed": "/feeds/local",
  "consumers": [
    { "package": "FsHotWatch.Cli", "repo": "/repos/private-app",
      "pin": ".config/dotnet-tools.json", "gate": "./build.fsx check", "timeoutMinutes": 90 },
    { "package": "TestPrune.Core", "repo": "/repos/FsHotWatch",
      "pin": "src/FsHotWatch.TestPrune/FsHotWatch.TestPrune.fsproj", "gate": "mise run ci",
      "timeoutMinutes": 30, "revision": "main@origin" }
  ]
}
"""

let private pkg (name: string) : PackageConfig =
    {
        Name = name
        Fsproj = sprintf "src/%s/%s.fsproj" name name
        DllPath = sprintf "src/%s/bin/Release/net10.0/%s.dll" name name
        TagPrefix = name.ToLowerInvariant() + "-v"
        FsProjsSharingSameTag = []
    }

let private v (s: string) : Version = parse s

let private occurrences (needle: string) (haystack: string) : int = haystack.Split(needle).Length - 1

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
    test <@ first.Repo = "/repos/private-app" @>
    test <@ first.Pin = ".config/dotnet-tools.json" @>
    test <@ first.Gate = "./build.fsx check" @>
    test <@ first.Timeout = TimeSpan.FromMinutes 90.0 @>
    test <@ first.Revision = None @>
    test <@ config.Consumers[1].Revision = Some "main@origin" @>

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
        [
            pkg "TestPrune.Core", v "12.0.0"
            pkg "fshotwatch.cli", v "0.15.0-alpha.1"
            pkg "TestPrune.Sql", v "1.0.0"
        ]

    let selected =
        selectConsumers config plan
        |> List.map (fun (p, version, c) -> p.Name, format version, c.Repo)

    let expected =
        [
            "TestPrune.Core", "12.0.0", "/repos/FsHotWatch"
            "fshotwatch.cli", "0.15.0-alpha.1", "/repos/private-app"
        ]

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

// ---- the seed project ----

[<Fact>]
let ``seedProject - downloads exactly the candidate, for the running runtime's TFM`` () =
    let content = seedProject "FsHotWatch.Cli" "0.14.0-alpha.130"
    test <@ content.Contains """<PackageDownload Include="FsHotWatch.Cli" Version="[0.14.0-alpha.130]" />""" @>
    test <@ content.Contains(sprintf "<TargetFramework>net%d.0</TargetFramework>" Environment.Version.Major) @>
    test <@ not (content.Contains "PackageReference") @>

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
    {
        RunIn =
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
                gate command
    }

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
        {
            ConfigPath = Path.Combine(dir, ".fssemantictagger.json")
            Skip = false
            LogDir = Path.Combine(root, "artifacts", "consumer-canary")
            ScratchDir = Path.Combine(dir, "scratch")
            PackagesCache = Path.Combine(dir, "cache")
            Ops = fakeOps commands gate
        }

    let config =
        {
            LocalFeed = Path.Combine(dir, "feed")
            Consumers =
                [
                    {
                        Package = "TestPrune.Core"
                        Repo = repo
                        Pin = "src/C.fsproj"
                        Gate = "mise run ci"
                        Timeout = TimeSpan.FromMinutes 1.0
                        Revision = Some "main"
                        Workspace = None
                        Teardown = None
                    }
                ]
        }

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
let ``run - edits the pin, seeds the candidate from the scratch dir, then runs the gate in the workspace`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let pinned = File.ReadAllText(Path.Combine(workspace, "src", "C.fsproj"))
        test <@ pinned.Contains """<PackageReference Include="TestPrune.Core" Version="12.0.0" />""" @>
        test <@ not (File.Exists(Path.Combine(workspace, "nuget.config"))) @>
        let seedDir = Path.Combine(settings.ScratchDir, "testprune.core-12.0.0")

        test
            <@
                (File.ReadAllText(Path.Combine(seedDir, "seed.csproj"))).Contains
                    """<PackageDownload Include="TestPrune.Core" Version="[12.0.0]" />"""
            @>

        let logged = commands |> Seq.filter (fun c -> c.Contains ": sh -c") |> List.ofSeq

        test <@ logged.Length = 2 @>
        test <@ logged[0].StartsWith(seedDir + ": sh -c dotnet restore seed.csproj") @>
        test <@ logged[1].StartsWith(workspace + ": sh -c mise run ci") @>
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

/// The scaffold's consumer, re-read from a config that names no revision. A
/// `.jj` marker beside the `.git` one makes it a (colocated) jj consumer.
let private withoutRevision (dir: string) (repo: string) (jj: bool) : CanaryConfig =
    if jj then
        Directory.CreateDirectory(Path.Combine(repo, ".jj")) |> ignore

    let json =
        sprintf
            """{ "localFeed": %s, "consumers": [ { "package": "TestPrune.Core", "repo": %s, "pin": "src/C.fsproj", "gate": "mise run ci", "timeoutMinutes": 1 } ] }"""
            (System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "feed")))
            (System.Text.Json.JsonSerializer.Serialize repo)

    match parseConfig configPath json with
    | Ok c -> c
    | Error e -> failwith e

[<Fact>]
let ``toJson - an absent revision stays absent`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let _, repo, _, _ = scaffold dir (fun _ -> Exited 0) commands
        let config = withoutRevision dir repo false
        let json = toJson config
        test <@ not (json.Contains "revision") @>
        test <@ parseConfig configPath json = Ok config @>)

let private indexOf (commands: ResizeArray<string>) (expected: string) : int =
    commands.FindIndex(fun c -> c = expected)

[<Fact>]
let ``run - a jj consumer with no revision fetches, then works off the remote's main`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, _ = scaffold dir (fun _ -> Exited 0) commands
        let config = withoutRevision dir repo true
        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let fetched = indexOf commands (sprintf "%s: jj git fetch --remote origin" repo)

        let added =
            commands.FindIndex(fun c ->
                c.StartsWith(sprintf "%s: jj workspace add " repo)
                && c.EndsWith(sprintf " -r main@origin %s" workspace))

        test <@ fetched >= 0 @>
        test <@ added > fetched @>)

[<Fact>]
let ``run - a git consumer with no revision fetches, then works off origin/main`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, _ = scaffold dir (fun _ -> Exited 0) commands
        let config = withoutRevision dir repo false
        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let fetched = indexOf commands (sprintf "%s: git fetch origin" repo)

        let added =
            indexOf commands (sprintf "%s: git worktree add --detach %s origin/main" repo workspace)

        test <@ fetched >= 0 @>
        test <@ added > fetched @>)

[<Fact>]
let ``run - an explicit revision is used as written and nothing is fetched`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, config = scaffold dir (fun _ -> Exited 0) commands

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        test <@ not (commands |> Seq.exists (fun c -> c.Contains " fetch")) @>)

[<Fact>]
let ``run - a failed fetch refuses before any workspace is created`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, _ = scaffold dir (fun _ -> Exited 0) commands
        let config = withoutRevision dir repo false

        let offline =
            { settings with
                Ops =
                    { settings.Ops with
                        RunIn =
                            fun cwd cmd args ->
                                if cmd = "git" && args.StartsWith "fetch" then
                                    commands.Add(sprintf "%s: %s %s" cwd cmd args)
                                    Failure("could not resolve host", 128)
                                else
                                    settings.Ops.RunIn cwd cmd args
                    }
            }

        match run offline root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> failwith "expected a refusal"
        | Error refusal ->
            let message = formatRefusal refusal
            test <@ message.Contains "git fetch origin" @>
            test <@ message.Contains "could not resolve host" @>
            test <@ not (commands |> Seq.exists (fun c -> c.Contains "worktree add")) @>)

// ---- the decision the release makes ----

[<Fact>]
let ``decide - absent config skips with a note naming the path`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, _ = scaffold dir (fun _ -> Exited 0) commands

        let output, verdict =
            withCapturedConsole (fun () -> decide settings root [ pkg "TestPrune.Core", v "12.0.0" ])

        test <@ verdict = Ok(Skipped(NoConsumerConfig settings.ConfigPath)) @>
        test <@ (formatVerdict (Skipped(NoConsumerConfig settings.ConfigPath))).Contains settings.ConfigPath @>
        // The release prints the verdict; decide does not print it a second time.
        test <@ output = "" @>)

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
    {
        RunIn =
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
                    Shell.runLogged cwd command timeout logPath
    }

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
            [
                "init -q -b main"
                "-c user.email=t@example.com -c user.name=t add ."
                "-c user.email=t@example.com -c user.name=t commit -q -m init"
            ] do
            Shell.runOrFail "git" (sprintf "-C %s %s" repo args) |> ignore

    repo

let private integrationRun (dir: string) (gateExit: int) =
    let repo = gitRepoWithGate dir gateExit
    let root = Path.Combine(dir, "producer")
    Directory.CreateDirectory root |> ignore

    let settings =
        {
            ConfigPath = Path.Combine(dir, ".fssemantictagger.json")
            Skip = false
            LogDir = Path.Combine(root, "artifacts", "consumer-canary")
            ScratchDir = Path.Combine(dir, "scratch")
            PackagesCache = Path.Combine(dir, "cache")
            Ops = integrationOps
        }

    let config =
        {
            LocalFeed = Path.Combine(dir, "feed")
            Consumers =
                [
                    {
                        Package = "TestPrune.Core"
                        Repo = repo
                        Pin = "src/C.fsproj"
                        Gate = "sh gate.sh"
                        Timeout = TimeSpan.FromMinutes 1.0
                        Revision = Some "main"
                        Workspace = None
                        Teardown = None
                    }
                ]
        }

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
        {
            Packages =
                [
                    {
                        Name = "MyLib"
                        Fsproj = fsproj
                        DllPath = "bin/Release/net10.0/MyLib.dll"
                        TagPrefix = "v"
                        FsProjsSharingSameTag = []
                    }
                ]
            ReservedVersions = Set.empty
            PreBuildCmds = []
            PublishWorkflows = defaultPublishWorkflows
            CiTimeout = None
            RootDir = dir
        }

    let output, code =
        withCapturedConsole (fun () ->
            Release.release
                {
                    Run = fakeRun
                    Config = config
                    Command = Release.StartAlpha
                    Mode = mode
                    TargetPackages = []
                    ExtractPrevious = noPrevious
                    ExtractCachedPrevious = noCachedPrevious
                    ExtractCurrent = noCurrent
                    CiPollIntervalMs = 0
                    CiWait = CiWaitTests.fixedCiWait 0 10
                    TagPush =
                        {
                            PushAttempts = 1
                            PushRetryDelayMs = 0
                            RunPollIntervalMs = 0
                            RunPollAttempts = 1
                        }
                    CheckFeedPresence = fun _ _ -> Api.OnFeed
                    CheckRestorable = fun _ _ _ -> Api.OnFeed
                    WaitForNuGet = false
                    NuGetPollIntervalMs = 0
                    NuGetMaxAttempts = 1
                    Push = false
                    Check = false
                    Canary = canary
                })

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
        {
            LocalFeed = Path.Combine(dir, "feed")
            Consumers =
                [
                    {
                        Package = "MyLib"
                        Repo = repo
                        Pin = "src/C.fsproj"
                        Gate = "mise run ci"
                        Timeout = TimeSpan.FromMinutes 1.0
                        Revision = Some "main"
                        Workspace = None
                        Teardown = None
                    }
                ]
        }

    let configPath = Path.Combine(dir, ".fssemantictagger.json")
    File.WriteAllText(configPath, toJson config)

    {
        ConfigPath = configPath
        Skip = false
        LogDir = Path.Combine(dir, "logs")
        ScratchDir = Path.Combine(dir, "scratch")
        PackagesCache = Path.Combine(dir, "cache")
        Ops = fakeOps commands gate
    }

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
                Skip = true
            }

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
        test <@ occurrences "Consumer canary: skipped" output = 1 @>
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
                Skip = true
            }

        let _, output, _, _ = releaseWith dir canary Release.DryRun
        test <@ output.Contains "SKIPPED by --skip-consumer-canary" @>

        let unrelated =
            {
                LocalFeed = Path.Combine(dir, "feed")
                Consumers =
                    [
                        {
                            Package = "Other"
                            Repo = dir
                            Pin = "x.fsproj"
                            Gate = "true"
                            Timeout = TimeSpan.FromMinutes 1.0
                            Revision = Some "main"
                            Workspace = None
                            Teardown = None
                        }
                    ]
            }

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
            {
                LocalFeed = Path.Combine(dir, "feed")
                Consumers =
                    [
                        {
                            Package = "Other"
                            Repo = dir
                            Pin = "x.fsproj"
                            Gate = "true"
                            Timeout = TimeSpan.FromMinutes 1.0
                            Revision = Some "main"
                            Workspace = None
                            Teardown = None
                        }
                    ]
            }

        File.WriteAllText(canary.ConfigPath, toJson unrelated)
        let code, output, _, _ = releaseWith dir canary Release.PushTags
        test <@ code = 0 @>
        test <@ output.Contains "names no consumer of these packages" @>
        test <@ occurrences "Consumer canary: skipped" output = 1 @>
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
let ``formatRefusal - every reason reads as a sentence with the consumer and package`` () =
    let refusal reason =
        {
            Package = pkg "P"
            Version = v "1.0.0"
            Consumer =
                {
                    Package = "P"
                    Repo = "/repos/c"
                    Pin = "p.fsproj"
                    Gate = "mise run ci"
                    Timeout = TimeSpan.FromMinutes 1.0
                    Revision = Some "main"
                    Workspace = None
                    Teardown = None
                }
            Reason = reason
            LogPath = None
            Workspace = None
        }

    let expectations =
        [
            PackFailed "boom", "packing the candidate failed: boom"
            WorkspaceFailed "no", "creating the workspace failed: no"
            PinFailed "bad", "pinning the candidate failed: bad"
            RestoreFailed 2, "restoring the candidate exited 2"
            RestoreTimedOut(TimeSpan.FromSeconds 90.0), "restoring the candidate exceeded its 1m30s budget"
            GateFailed 3, "gate `mise run ci` exited 3"
            GateTimedOut(TimeSpan.FromMinutes 2.0), "gate `mise run ci` exceeded its 2m0s budget"
        ]

    for (reason, phrase) in expectations do
        let text = formatRefusal (refusal reason)
        test <@ text.Contains phrase && text.Contains "/repos/c (P 1.0.0)" @>
        test <@ not (text.Contains "log:") && not (text.Contains "workspace:") @>

[<Fact>]
let ``formatVerdict - the skip and empty verdicts name what happened`` () =
    test <@ (formatVerdict (Skipped(NoConsumersForPlan "/c.json"))).Contains "/c.json names no consumer" @>
    test <@ formatVerdict (Passed []) = "Consumer canary: nothing to run" @>

[<Fact>]
let ``defaultSettings - reads the home config, logs per releasing repo and seeds from the temp dir`` () =
    let settings = defaultSettings true "/repo"
    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    test <@ settings.Skip @>
    test <@ Path.GetFileName settings.ConfigPath = ".fssemantictagger.json" @>
    test <@ settings.LogDir = Path.Combine(home, ".fssemantictagger", "logs", "repo") @>
    test <@ settings.ScratchDir.StartsWith(Path.GetTempPath()) @>
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

        let unpinned =
            { config with
                Consumers =
                    [
                        { config.Consumers[0] with
                            Revision = None
                        }
                    ]
            }

        match run settings root unpinned [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = WorkspaceFailed msg } -> test <@ msg.Contains "neither a jj nor a git repository" @>
        | other -> failwithf "unexpected %A" other

        test <@ not (commands |> Seq.exists (fun c -> c.Contains " fetch")) @>

        let missing =
            { config with
                Consumers =
                    [
                        { config.Consumers[0] with
                            Repo = Path.Combine(dir, "nowhere")
                        }
                    ]
            }

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
                                    settings.Ops.RunIn cwd cmd args
                    }
            }

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
let ``run - a pin missing from the workspace, a seed timeout, and an existing nuget.config left alone`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()

        let root, repo, settings, config =
            scaffold
                dir
                (fun c ->
                    if c.StartsWith "dotnet restore" then
                        TimedOut(TimeSpan.FromMinutes 1.0)
                    else
                        Exited 0)
                commands

        let absent =
            { config with
                Consumers =
                    [
                        { config.Consumers[0] with
                            Pin = "src/Missing.fsproj"
                        }
                    ]
            }

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
                    [
                        { config.Consumers[0] with
                            Pin = "dotnet-tools.json"
                        }
                    ]
            }

        match run settings root manifest [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = RestoreTimedOut _ } -> ()
        | other -> failwithf "unexpected %A" other

        let workspace = repo + "-canary-testprune-core"
        test <@ not (commands |> Seq.exists (fun c -> c.Contains "mise run ci")) @>

        test
            <@
                File.ReadAllText(Path.Combine(workspace, "NuGet.Config")) =
                    File.ReadAllText(Path.Combine(repo, "NuGet.Config"))
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

/// Kills the background `sleep` a test command recorded in `<dir>/sleep.pid`.
let private killRecordedSleep (dir: string) =
    Shell.runIn dir "sh" "-c \"kill $(cat sleep.pid) 2>/dev/null\"" |> ignore

/// Whether the `sleep` recorded in `<dir>/sleep.pid` is still running a few
/// seconds on. Asks `sh`: on Windows the recorded pid is an MSYS2 pid.
let private recordedSleepSurvives (dir: string) =
    let alive () =
        Shell.runIn dir "sh" "-c \"kill -0 $(cat sleep.pid) 2>/dev/null\""
        |> (function
        | Success _ -> true
        | Failure _ -> false)

    let deadline = Diagnostics.Stopwatch.StartNew()

    while alive () && deadline.Elapsed < TimeSpan.FromSeconds 5.0 do
        Threading.Thread.Sleep 100

    alive ()

let private truncationNote = "stopped reading output"

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``runLogged - a command past its budget is killed and the log says so`` () =
    withTempDir (fun dir ->
        let log = Path.Combine(dir, "logs", "gate.log")
        let clock = Diagnostics.Stopwatch.StartNew()

        try
            let outcome =
                Shell.runLogged
                    dir
                    "echo started; sleep 30 & echo $! > sleep.pid; wait $!; echo never"
                    (TimeSpan.FromSeconds 1.0)
                    log

            test <@ clock.Elapsed < TimeSpan.FromSeconds 20.0 @>
            test <@ outcome = TimedOut(TimeSpan.FromSeconds 1.0) @>
            // Git for Windows: MSYS2 children are outside the tree `Kill` walks.
            test <@ not (recordedSleepSurvives dir) @>
            let text = File.ReadAllText log
            test <@ text.Contains "started" @>
            test <@ text.Contains "killed after 0m1s" @>
            test <@ not (text.Contains "\nnever") @>
        finally
            killRecordedSleep dir)

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``runLogged - a background process holding the pipes does not stall a finished gate`` () =
    withTempDir (fun dir ->
        let log = Path.Combine(dir, "gate.log")
        let clock = Diagnostics.Stopwatch.StartNew()

        try
            let outcome =
                Shell.runLoggedWithin
                    (TimeSpan.FromSeconds 1.0)
                    dir
                    "sleep 30 & echo $! > sleep.pid; echo done"
                    (TimeSpan.FromMinutes 1.0)
                    log

            test <@ clock.Elapsed < TimeSpan.FromSeconds 10.0 @>
            test <@ outcome = Exited 0 @>
            let text = File.ReadAllText log
            test <@ text.Contains "done" @>
            test <@ text.Contains truncationNote @>
        finally
            killRecordedSleep dir)

[<Xunit.Fact(Timeout = IntegrationTimeoutMs)>]
let ``runLogged - a gate that leaves nothing behind is logged in full`` () =
    withTempDir (fun dir ->
        let log = Path.Combine(dir, "gate.log")

        let outcome =
            Shell.runLogged
                dir
                "for i in 1 2 3; do echo out$i; echo err$i >&2; done; exit 4"
                (TimeSpan.FromMinutes 1.0)
                log

        test <@ outcome = Exited 4 @>
        let text = File.ReadAllText log

        test <@ [ "out1"; "out2"; "out3"; "err1"; "err2"; "err3" ] |> List.forall text.Contains @>

        test <@ not (text.Contains truncationNote) @>)

// ---- AUTOMATION-1365: what the live proof on a real consumer found ----

[<Fact>]
let ``run - the candidate is restored outside the consumer workspace, with the feed as its only source`` () =
    // Blocker 1: a restore in the workspace runs under the consumer's global.json
    // with the releaser's SDK, so a consumer pinned to another SDK band exits 155.
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        let workspace = repo + "-canary-testprune-core"

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let restores =
            commands
            |> Seq.filter (fun c -> c.Contains ": sh -c dotnet restore")
            |> List.ofSeq

        test <@ restores.Length = 1 @>
        test <@ not (restores[0].StartsWith workspace) @>
        test <@ restores[0].Contains(sprintf "--source '%s'" config.LocalFeed) @>
        test <@ restores[0].Contains(sprintf "--packages '%s'" settings.PackagesCache) @>
        test <@ not (commands |> Seq.exists (fun c -> c.StartsWith(workspace + ": sh -c dotnet"))) @>)

[<Fact>]
let ``run - the consumer's tracked nuget.config is left byte-for-byte as committed`` () =
    // Blocker 2: the feed written into a tracked nuget.config is a filesystem
    // source the consumer's own gate may forbid, so the gate is red before tests run.
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands

        let tracked =
            "<configuration>\n  <packageSources>\n    <clear />\n  </packageSources>\n</configuration>\n"

        File.WriteAllText(Path.Combine(repo, "nuget.config"), tracked)

        match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let workspace = repo + "-canary-testprune-core"
        test <@ File.ReadAllText(Path.Combine(workspace, "nuget.config")) = tracked @>)

/// Make `dir` impossible to delete recursively, as a live process re-creating
/// files in it does; `unlock` undoes it.
let private lockAgainstDelete (dir: string) : unit -> unit =
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, "held")
    File.WriteAllText(file, "held")

    if OperatingSystem.IsWindows() then
        File.SetAttributes(file, FileAttributes.ReadOnly)
        fun () -> File.SetAttributes(file, FileAttributes.Normal)
    else
        File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        fun () ->
            File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

[<Fact>]
let ``run - a leftover workspace that cannot be removed refuses instead of crashing`` () =
    // Blocker 4: a daemon left alive by the previous gate kept the leftover
    // workspace non-empty, and the release died with an unhandled IOException.
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands
        let workspace = repo + "-canary-testprune-core"
        let unlock = lockAgainstDelete (Path.Combine(workspace, "busy"))

        try
            match run settings root config [ pkg "TestPrune.Core", v "12.0.0" ] with
            | Error { Reason = WorkspaceFailed msg } -> test <@ msg.Contains workspace @>
            | other -> failwithf "unexpected %A" other

            test <@ not (commands |> Seq.exists (fun c -> c.Contains "mise run ci")) @>
        finally
            unlock ())

[<Fact>]
let ``defaultSettings - logs outside the releasing repo, so a refusal leaves its tree clean`` () =
    // Blocker 5: a log under the releasing repo is an untracked file, and the next
    // release stops on "uncommitted changes".
    let root = Path.Combine(Path.GetTempPath(), "some-producer")
    let settings = defaultSettings false root
    let relative = Path.GetRelativePath(root, settings.LogDir)
    test <@ relative.StartsWith ".." || Path.IsPathRooted relative @>

[<Fact>]
let ``run - the unpublished candidate is evicted from the NuGet cache when the canary ends, passed or refused`` () =
    // Blocker 7: a candidate left in the cache at the version the real release
    // reuses can be restored later in place of the published package.
    for gateExit in [ 0; 3 ] do
        withTempDir (fun dir ->
            let commands = ResizeArray()
            let cached = Path.Combine(dir, "cache", "testprune.core", "12.0.0")

            let gate (command: string) =
                if command.StartsWith "dotnet " then
                    Directory.CreateDirectory cached |> ignore
                    Exited 0
                else
                    Exited gateExit

            let root, _, settings, config = scaffold dir gate commands
            let result = run settings root config [ pkg "TestPrune.Core", v "12.0.0" ]
            test <@ Result.isOk result = (gateExit = 0) @>
            test <@ commands |> Seq.exists (fun c -> c.Contains "mise run ci") @>
            test <@ not (Directory.Exists cached) @>)

[<Fact>]
let ``parseConfig - reads an optional workspace and teardown, and toJson round-trips them`` () =
    let json =
        """{ "consumers": [ { "package": "P", "repo": "/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 1,
             "workspace": "~/ws", "teardown": "fshw stop" },
           { "package": "Q", "repo": "/r", "pin": "p.fsproj", "gate": "true", "timeoutMinutes": 1,
             "workspace": ".workspaces/canary-q" } ] }"""

    let config =
        match parseConfig configPath json with
        | Ok c -> c
        | Error e -> failwith e

    let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
    test <@ config.Consumers[0].Workspace = Some(Path.Combine(home, "ws")) @>
    test <@ config.Consumers[0].Teardown = Some "fshw stop" @>
    test <@ config.Consumers[1].Workspace = Some ".workspaces/canary-q" @>
    test <@ config.Consumers[1].Teardown = None @>
    test <@ parseConfig configPath (toJson config) = Ok config @>

    match parseConfig configPath (json.Replace("\"fshw stop\"", "3")) with
    | Error msg -> test <@ msg.Contains "\"teardown\" must be a string" @>
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``run - a configured relative workspace is created inside the consumer's checkout`` () =
    // Blocker 3: tooling that finds its main checkout only among a workspace's
    // ancestors (intelligence's dotenv) sees nothing from a sibling directory.
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, repo, settings, config = scaffold dir (fun _ -> Exited 0) commands

        let inside =
            { config with
                Consumers =
                    [
                        { config.Consumers[0] with
                            Workspace = Some(Path.Combine(".workspaces", "canary"))
                        }
                    ]
            }

        let workspace = Path.Combine(repo, ".workspaces", "canary")

        match run settings root inside [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        test <@ workspacePathFor inside.Consumers[0] = workspace @>

        test
            <@
                commands
                |> Seq.exists (fun c -> c = sprintf "%s: git worktree add --detach %s main" repo workspace)
            @>

        test <@ (File.ReadAllText(Path.Combine(workspace, "src", "C.fsproj"))).Contains "12.0.0" @>

        test
            <@
                commands
                |> Seq.exists (fun c -> c.StartsWith(workspace + ": sh -c mise run ci"))
            @>

        test <@ not (Directory.Exists(repo + "-canary-testprune-core")) @>
        let absolute = Path.Combine(dir, "elsewhere")

        test
            <@
                workspacePathFor
                    { inside.Consumers[0] with
                        Workspace = Some absolute
                    }
                    =
                    absolute
            @>)

[<Fact>]
let ``run - the teardown runs after the gate, passed or refused, and in a leftover before it is replaced`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let gateExit = ref 3

        let root, repo, settings, config =
            scaffold
                dir
                (fun c ->
                    if c = "mise run ci" then
                        Exited gateExit.Value
                    else
                        Exited 0)
                commands

        let withTeardown =
            { config with
                Consumers =
                    [
                        { config.Consumers[0] with
                            Teardown = Some "stop-daemon"
                        }
                    ]
            }

        let workspace = repo + "-canary-testprune-core"
        let teardown = sprintf "%s: sh -c stop-daemon" workspace
        let gate = sprintf "%s: sh -c mise run ci" workspace

        match run settings root withTeardown [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Error { Reason = GateFailed 3 } -> ()
        | other -> failwithf "unexpected %A" other

        let first = commands |> Seq.filter (fun c -> c.StartsWith teardown) |> List.ofSeq
        test <@ first.Length = 1 @>
        let tornDownAt = commands.FindIndex(fun c -> c.StartsWith teardown)
        let gatedAt = commands.FindIndex(fun c -> c.StartsWith gate)
        test <@ tornDownAt > gatedAt @>

        commands.Clear()
        gateExit.Value <- 0

        match run settings root withTeardown [ pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok _ -> ()
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        let indices =
            commands
            |> Seq.indexed
            |> Seq.filter (fun (_, c) -> c.StartsWith teardown || c.StartsWith gate || c.Contains "worktree add")
            |> Seq.map (fun (_, c) ->
                if c.StartsWith teardown then "teardown"
                elif c.StartsWith gate then "gate"
                else "add")
            |> List.ofSeq

        test <@ indices = [ "teardown"; "add"; "gate"; "teardown" ] @>)

[<Fact>]
let ``run - a plan with packages that have no consumer packs and gates only the consumed one`` () =
    withTempDir (fun dir ->
        let commands = ResizeArray()
        let root, _, settings, config = scaffold dir (fun _ -> Exited 0) commands

        match run settings root config [ pkg "Other", v "1.0.0"; pkg "TestPrune.Core", v "12.0.0" ] with
        | Ok passed -> test <@ passed |> List.map (fun (p, _, _) -> p.Name) = [ "TestPrune.Core" ] @>
        | Error refusal -> failwithf "%s" (formatRefusal refusal)

        test <@ not (commands |> Seq.exists (fun c -> c.Contains "src/Other/Other.fsproj")) @>)

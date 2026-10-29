/// The consumer canary: before a release pushes any tag, each planned package
/// with a configured consumer is packed at its planned version into a local
/// feed, and the consumer's own gate is run in a fresh workspace pinned to that
/// candidate. The repo's own suite on its own tree cannot see a regression that
/// only appears at the consumer's scale; the consumer's gate can.
///
/// The configuration is machine-local (`~/.fssemantictagger.json`), because
/// which consumers are checked out on a machine is a fact about the machine.
module FsSemanticTagger.ConsumerCanary

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version

/// One consumer of one package: where its checkout is, which file pins the
/// package, and the command that decides whether the candidate is acceptable.
type Consumer =
    {
        /// The package id, matched case-insensitively against the plan.
        Package: string
        /// The consumer's repository root, `~` expanded.
        Repo: string
        /// The pin file, relative to `Repo`.
        Pin: string
        /// The gate, run through `/bin/sh -c` in the workspace.
        Gate: string
        Timeout: TimeSpan
        /// The revision the fresh workspace is created from. `None` is the
        /// remote's `main` after a fetch: `main@origin` in jj, `origin/main` in
        /// git. A local `main` can sit behind the remote's.
        Revision: string option
        /// Where the fresh workspace is created: relative to `Repo`, or absolute
        /// (`~` expanded). `None` is `<repo>-canary-<package>`, beside the checkout.
        /// A consumer whose tooling finds its main checkout only from inside it
        /// (e.g. `.workspaces/`) needs it there; it must be ignored by the consumer.
        Workspace: string option
        /// Run through `/bin/sh -c` in the workspace after the gate, passed or
        /// refused, and in a leftover workspace before it is replaced: it stops
        /// anything the gate left running there (a daemon), so the workspace can
        /// be removed. Best effort; its exit code is logged, not judged.
        Teardown: string option
    }

type CanaryConfig =
    {
        /// The directory the candidate is packed into and the workspace restores from.
        LocalFeed: string
        Consumers: Consumer list
    }

/// What `loadConfig` found at the config path. An absent file is a machine
/// without consumers, which is not an error.
type ConfigOutcome =
    | NoConfig of path: string
    | Loaded of CanaryConfig

/// The two pin shapes: a tool manifest's `"version"` and an MSBuild
/// `<PackageReference Version="...">`.
type PinFile =
    | ToolManifest of path: string
    | MsBuildProject of path: string

/// The process seam. `RunIn` is a plain command in a working directory; `RunGate`
/// is a shell command line whose output goes to a log file and whose runtime is
/// bounded. Everything else the canary does is file edits.
[<NoEquality; NoComparison>]
type Ops =
    {
        RunIn: string -> string -> string -> CommandResult
        RunGate: string -> string -> TimeSpan -> string -> GateOutcome
    }

[<NoEquality; NoComparison>]
type Settings =
    {
        ConfigPath: string
        /// `--skip-consumer-canary`.
        Skip: bool
        /// Where each consumer's restore-and-gate log is written. Outside the
        /// releasing repo, so a refusal leaves no untracked file in its tree.
        LogDir: string
        /// Where the candidate is restored into the cache from: a directory with
        /// no `global.json` above it, so the restore uses the releaser's SDK
        /// rather than whatever SDK the consumer's checkout requests.
        ScratchDir: string
        /// NuGet's global package cache, whose entry for the candidate must be
        /// evicted: NuGet never re-extracts a version it has already cached.
        PackagesCache: string
        Ops: Ops
    }

type RefusalReason =
    | PackFailed of error: string
    | WorkspaceFailed of error: string
    | PinFailed of error: string
    | RestoreFailed of exitCode: int
    | RestoreTimedOut of budget: TimeSpan
    | GateFailed of exitCode: int
    | GateTimedOut of budget: TimeSpan

type Refusal =
    {
        Package: PackageConfig
        Version: Version
        Consumer: Consumer
        Reason: RefusalReason
        LogPath: string option
        Workspace: string option
    }

type SkipReason =
    | NoConsumerConfig of path: string
    | NoConsumersForPlan of path: string
    | BreakGlass

type Verdict =
    | Skipped of SkipReason
    | Passed of (PackageConfig * Version * Consumer) list

let private expandHome (path: string) : string =
    if path = "~" || path.StartsWith "~/" then
        Path.Combine(
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
            path.TrimStart('~').TrimStart('/')
        )
    else
        path

let defaultLocalFeed: string =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".fssemantictagger", "feed")

let defaultConfigPath: string =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".fssemantictagger.json")

let defaultPackagesCache: string =
    match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
    | null
    | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
    | dir -> dir

/// Parse the machine-local config. Every error names the file and the field, so
/// a typo is never read as "no consumers".
let parseConfig (path: string) (json: string) : Result<CanaryConfig, string> =
    let field (where: string) (name: string) (el: JsonElement) : Result<JsonElement, string> =
        match el.TryGetProperty name with
        | true, v -> Ok v
        | _ -> Error(sprintf "%s: %s is missing \"%s\"" path where name)

    let str (where: string) (name: string) (el: JsonElement) : Result<string, string> =
        field where name el
        |> Result.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then
                Ok(v.GetString())
            else
                Error(sprintf "%s: %s.%s must be a string" path where name))

    let maybeStr (name: string) (el: JsonElement) : Result<string option, string> =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
        | true, _ -> Error(sprintf "%s: \"%s\" must be a string" path name)
        | _ -> Ok None

    let optionalStr (name: string) (fallback: string) (el: JsonElement) : Result<string, string> =
        maybeStr name el |> Result.map (Option.defaultValue fallback)

    let consumer (index: int) (el: JsonElement) : Result<Consumer, string> =
        let where = sprintf "consumers[%d]" index

        str where "package" el
        |> Result.bind (fun package ->
            str where "repo" el
            |> Result.bind (fun repo ->
                str where "pin" el
                |> Result.bind (fun pin ->
                    str where "gate" el
                    |> Result.bind (fun gate ->
                        field where "timeoutMinutes" el
                        |> Result.bind (fun t ->
                            match t.ValueKind with
                            | JsonValueKind.Number when t.GetDouble() > 0.0 ->
                                Ok(TimeSpan.FromMinutes(t.GetDouble()))
                            | _ -> Error(sprintf "%s: %s.timeoutMinutes must be a positive number" path where))
                        |> Result.bind (fun timeout ->
                            maybeStr "revision" el
                            |> Result.bind (fun revision ->
                                maybeStr "workspace" el
                                |> Result.bind (fun workspace ->
                                    maybeStr "teardown" el
                                    |> Result.map (fun teardown ->
                                        {
                                            Package = package
                                            Repo = expandHome repo
                                            Pin = pin
                                            Gate = gate
                                            Timeout = timeout
                                            Revision = revision
                                            Workspace = workspace |> Option.map expandHome
                                            Teardown = teardown
                                        }))))))))

    let parsed =
        try
            Ok(JsonDocument.Parse json)
        with :? JsonException as e ->
            Error(sprintf "%s: not valid JSON: %s" path e.Message)

    parsed
    |> Result.bind (fun doc ->
        let root = doc.RootElement

        optionalStr "localFeed" defaultLocalFeed root
        |> Result.bind (fun feed ->
            field "the file" "consumers" root
            |> Result.bind (fun consumers ->
                if consumers.ValueKind <> JsonValueKind.Array then
                    Error(sprintf "%s: \"consumers\" must be an array" path)
                else
                    consumers.EnumerateArray()
                    |> Seq.mapi consumer
                    |> Seq.fold
                        (fun acc next ->
                            match acc, next with
                            | Ok list, Ok c -> Ok(c :: list)
                            | Error e, _
                            | _, Error e -> Error e)
                        (Ok [])
                    |> Result.map (fun consumers ->
                        {
                            LocalFeed = expandHome feed
                            Consumers = List.rev consumers
                        }))))

let loadConfig (path: string) : Result<ConfigOutcome, string> =
    if File.Exists path then
        parseConfig path (File.ReadAllText path) |> Result.map Loaded
    else
        Ok(NoConfig path)

/// The config as JSON, in the shape `parseConfig` reads.
let toJson (config: CanaryConfig) : string =
    let consumers =
        config.Consumers
        |> List.map (fun c ->
            {|
                package = c.Package
                repo = c.Repo
                pin = c.Pin
                gate = c.Gate
                timeoutMinutes = c.Timeout.TotalMinutes
                revision = c.Revision |> Option.toObj
                workspace = c.Workspace |> Option.toObj
                teardown = c.Teardown |> Option.toObj
            |})

    JsonSerializer.Serialize(
        {|
            localFeed = config.LocalFeed
            consumers = consumers
        |},
        JsonSerializerOptions(
            WriteIndented = true,
            NewLine = "\n",
            DefaultIgnoreCondition = Serialization.JsonIgnoreCondition.WhenWritingNull
        )
    )

/// Every (package, version, consumer) the plan touches, in plan order.
let selectConsumers
    (config: CanaryConfig)
    (plan: (PackageConfig * Version) list)
    : (PackageConfig * Version * Consumer) list =
    plan
    |> List.collect (fun (pkg, version) ->
        config.Consumers
        |> List.filter (fun c -> String.Equals(c.Package, pkg.Name, StringComparison.OrdinalIgnoreCase))
        |> List.map (fun c -> pkg, version, c))

let pinFileOf (pin: string) : Result<PinFile, string> =
    let name = Path.GetFileName pin

    if String.Equals(name, "dotnet-tools.json", StringComparison.OrdinalIgnoreCase) then
        Ok(ToolManifest pin)
    elif
        name.EndsWith ".fsproj"
        || name.EndsWith ".csproj"
        || name.EndsWith ".props"
        || name.EndsWith ".targets"
    then
        Ok(MsBuildProject pin)
    else
        Error(
            sprintf
                "pin %s: not a supported pin file (a dotnet-tools.json manifest or an MSBuild project/props file)"
                pin
        )

/// Rewrite only the version text of the package's pin; every other byte of the
/// file is kept.
let setPinVersion (pin: PinFile) (packageId: string) (version: string) (content: string) : Result<string, string> =
    let id = Regex.Escape packageId

    match pin with
    | ToolManifest path ->
        // The tool's key, then the first "version" inside its object.
        let entry =
            Regex(sprintf "\"%s\"\\s*:\\s*\\{[^}]*?\"version\"\\s*:\\s*\"(?<v>[^\"]*)\"" id, RegexOptions.IgnoreCase)

        let m = entry.Match content

        if m.Success then
            let v = m.Groups["v"]
            Ok(content.Substring(0, v.Index) + version + content.Substring(v.Index + v.Length))
        else
            Error(sprintf "pin %s: no tool \"%s\" with a \"version\" in the manifest" path packageId)
    | MsBuildProject path ->
        let reference =
            Regex(
                sprintf "<PackageReference\\b[^>]*\\bInclude\\s*=\\s*\"%s\"[^>]*\\bVersion\\s*=\\s*\"(?<v>[^\"]*)\"" id,
                RegexOptions.IgnoreCase
            )

        let matches = reference.Matches content

        if matches.Count = 0 then
            Error(sprintf "pin %s: no <PackageReference Include=\"%s\" Version=\"...\"> found" path packageId)
        else
            // Replace from the end so earlier offsets stay valid.
            let edited =
                matches
                |> Seq.map (fun m -> m.Groups["v"])
                |> Seq.sortByDescending (fun g -> g.Index)
                |> Seq.fold
                    (fun (text: string) g -> text.Substring(0, g.Index) + version + text.Substring(g.Index + g.Length))
                    content

            Ok edited

/// Single-quote `text` for `/bin/sh`.
let private shellQuote (text: string) : string = "'" + text.Replace("'", "'\\''") + "'"

/// A project whose only job is to put the candidate in the NuGet cache:
/// `PackageDownload` of the exact version, no dependencies, no compatibility
/// check, so it works for a tool package and a library alike. The TFM is the
/// running runtime's, which the SDK that runs the tagger always has.
let seedProject (packageId: string) (version: string) : string =
    String.concat
        "\n"
        [
            "<Project Sdk=\"Microsoft.NET.Sdk\">"
            "  <PropertyGroup>"
            sprintf "    <TargetFramework>net%d.0</TargetFramework>" Environment.Version.Major
            "  </PropertyGroup>"
            "  <ItemGroup>"
            sprintf "    <PackageDownload Include=\"%s\" Version=\"[%s]\" />" packageId version
            "  </ItemGroup>"
            "</Project>"
            ""
        ]

/// The fresh workspace's path: the consumer's `Workspace` (relative to `Repo`,
/// or absolute), else `<repo>-canary-<package>` beside the checkout.
let workspacePathFor (consumer: Consumer) : string =
    match consumer.Workspace with
    | Some path when Path.IsPathRooted path -> path
    | Some path -> Path.GetFullPath(Path.Combine(consumer.Repo, path))
    | None ->
        let slug = consumer.Package.ToLowerInvariant().Replace('.', '-')
        consumer.Repo.TrimEnd(Path.DirectorySeparatorChar) + "-canary-" + slug

let private workspaceName (consumer: Consumer) : string =
    "canary-" + consumer.Package.ToLowerInvariant().Replace('.', '-')

let logPathFor (logDir: string) (pkg: PackageConfig) (version: Version) (consumer: Consumer) : string =
    let consumerName =
        Path.GetFileName(consumer.Repo.TrimEnd(Path.DirectorySeparatorChar))

    Path.Combine(logDir, sprintf "%s-%s-%s.log" pkg.Name (format version) consumerName)

/// NuGet's cache entry for the candidate. NuGet never re-extracts a version it
/// has cached, and the real release reuses this version.
let private cachedCandidate (settings: Settings) (packageId: string) (version: Version) : string =
    Path.Combine(settings.PackagesCache, packageId.ToLowerInvariant(), (format version).ToLowerInvariant())

let private evict (settings: Settings) (packageId: string) (version: Version) : unit =
    let cached = cachedCandidate settings packageId version

    if Directory.Exists cached then
        Directory.Delete(cached, true)

/// Pack the candidate at its planned version into the feed and evict any cached
/// copy of that id+version. `-p:Version` because the fsproj is not bumped until
/// after the canary; `-p:ReleaseBuild=true` so RefStamp emits the clean version.
let private packCandidate
    (settings: Settings)
    (rootDir: string)
    (feed: string)
    (pkg: PackageConfig)
    (version: Version)
    : Result<unit, RefusalReason> =
    Directory.CreateDirectory feed |> ignore
    let versionText = format version
    evict settings pkg.Name version

    let args =
        sprintf "pack %s -c Release -p:ReleaseBuild=true -p:Version=%s -o %s" pkg.Fsproj versionText feed

    match settings.Ops.RunIn rootDir "dotnet" args with
    | Success _ -> Ok()
    | Failure(error, code) -> Error(PackFailed(sprintf "dotnet %s exited %d: %s" args code error))

/// How long a teardown may take before the canary moves on without it.
let teardownBudget = TimeSpan.FromMinutes 5.0

/// Run the consumer's teardown in `workspace`, if it has one and the workspace
/// exists. Its outcome is in the log; the canary does not judge it.
let private teardown (settings: Settings) (consumer: Consumer) (workspace: string) (logPath: string) : unit =
    match consumer.Teardown with
    | Some command when Directory.Exists workspace ->
        settings.Ops.RunGate workspace command teardownBudget logPath |> ignore
    | _ -> ()

/// Remove a leftover workspace. A process still writing in it (a daemon the
/// previous gate left alive) makes the delete fail; that is a refusal naming the
/// workspace, not a crash, and nothing has been written by then.
let private removeTree (path: string) : Result<unit, string> =
    let rec attempt (left: int) =
        try
            if Directory.Exists path then
                Directory.Delete(path, true)

            Ok()
        with
        | _ when left > 1 ->
            Threading.Thread.Sleep 500
            attempt (left - 1)
        | e ->
            Error(
                sprintf
                    "the leftover workspace %s could not be removed (%s); something may still be running in it. Stop it (a `teardown` in the consumer's config does this on every run), then re-run."
                    path
                    e.Message
            )

    attempt 3

/// A fresh workspace at `Revision`, at `workspacePathFor`. A leftover from an
/// earlier run (kept for inspection) is torn down and replaced.
let private createWorkspace
    (settings: Settings)
    (consumer: Consumer)
    (logPath: string)
    : Result<string, RefusalReason> =
    let ops = settings.Ops
    let path = workspacePathFor consumer
    let isJj = Directory.Exists(Path.Combine(consumer.Repo, ".jj"))

    let isGit =
        Directory.Exists(Path.Combine(consumer.Repo, ".git"))
        || File.Exists(Path.Combine(consumer.Repo, ".git"))

    let discardLeftover () =
        teardown settings consumer path logPath

        if isJj then
            ops.RunIn consumer.Repo "jj" (sprintf "workspace forget %s" (workspaceName consumer))
            |> ignore

        if isGit then
            ops.RunIn consumer.Repo "git" (sprintf "worktree remove --force %s" path)
            |> ignore

            ops.RunIn consumer.Repo "git" "worktree prune" |> ignore

        removeTree path

    let neither = sprintf "%s is neither a jj nor a git repository" consumer.Repo

    let fetch (cmd: string) (args: string) (remoteMain: string) : Result<string, string> =
        match ops.RunIn consumer.Repo cmd args with
        | Success _ -> Ok remoteMain
        | Failure(error, code) -> Error(sprintf "%s %s in %s exited %d: %s" cmd args consumer.Repo code error)

    let revision () : Result<string, string> =
        match consumer.Revision with
        | Some revision -> Ok revision
        | None when isJj -> fetch "jj" "git fetch --remote origin" "main@origin"
        | None when isGit -> fetch "git" "fetch origin" "origin/main"
        | None -> Error neither

    let add (revision: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        if isJj then
            ops.RunIn
                consumer.Repo
                "jj"
                (sprintf "workspace add --name %s -r %s %s" (workspaceName consumer) revision path)
        elif isGit then
            ops.RunIn consumer.Repo "git" (sprintf "worktree add --detach %s %s" path revision)
        else
            Failure(neither, 1)

    if not (Directory.Exists consumer.Repo) then
        Error(WorkspaceFailed(sprintf "consumer repo %s does not exist" consumer.Repo))
    else
        revision ()
        |> Result.bind (fun revision -> discardLeftover () |> Result.map (fun () -> revision))
        |> Result.mapError WorkspaceFailed
        |> Result.bind (fun revision ->
            match add revision with
            | Success _ -> Ok path
            | Failure(error, _) -> Error(WorkspaceFailed error))

/// Put the candidate in the NuGet cache from `ScratchDir`, with the feed as the
/// only source. Run outside the consumer's checkout, so its `global.json` cannot
/// select an SDK the releaser lacks, and its tracked `nuget.config` is never
/// edited; the consumer's own restore then finds the exact version in the cache.
let private seedCandidate
    (settings: Settings)
    (feed: string)
    (pkg: PackageConfig)
    (version: Version)
    (timeout: TimeSpan)
    (logPath: string)
    : Result<unit, RefusalReason> =
    let dir =
        Path.Combine(settings.ScratchDir, sprintf "%s-%s" (pkg.Name.ToLowerInvariant()) (format version))

    if Directory.Exists dir then
        Directory.Delete(dir, true)

    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "seed.csproj"), seedProject pkg.Name (format version))

    let command =
        sprintf
            "dotnet restore seed.csproj --source %s --packages %s"
            (shellQuote feed)
            (shellQuote settings.PackagesCache)

    match settings.Ops.RunGate dir command timeout logPath with
    | Exited 0 -> Ok()
    | Exited code -> Error(RestoreFailed code)
    | TimedOut budget -> Error(RestoreTimedOut budget)

/// Pin the candidate, put it in the cache, then gate; tear down after the gate.
let private runOne
    (settings: Settings)
    (feed: string)
    (pkg: PackageConfig)
    (version: Version)
    (consumer: Consumer)
    (workspace: string)
    (logPath: string)
    : Result<unit, RefusalReason> =
    pinFileOf consumer.Pin
    |> Result.mapError PinFailed
    |> Result.bind (fun pin ->
        let pinPath = Path.Combine(workspace, consumer.Pin)

        if not (File.Exists pinPath) then
            Error(PinFailed(sprintf "pin %s does not exist in the workspace %s" consumer.Pin workspace))
        else
            setPinVersion pin pkg.Name (format version) (File.ReadAllText pinPath)
            |> Result.mapError PinFailed
            |> Result.map (fun edited -> File.WriteAllText(pinPath, edited)))
    |> Result.bind (fun () -> seedCandidate settings feed pkg version consumer.Timeout logPath)
    |> Result.bind (fun () ->
        let outcome = settings.Ops.RunGate workspace consumer.Gate consumer.Timeout logPath
        teardown settings consumer workspace logPath

        match outcome with
        | Exited 0 -> Ok()
        | Exited code -> Error(GateFailed code)
        | TimedOut budget -> Error(GateTimedOut budget))

/// Run every consumer the plan selects, stopping at the first refusal. Each
/// package is packed once, however many consumers it has. When the canary ends,
/// passed or refused, every packed candidate is evicted from the NuGet cache:
/// it was never published, and the real release reuses its version.
let run
    (settings: Settings)
    (rootDir: string)
    (config: CanaryConfig)
    (plan: (PackageConfig * Version) list)
    : Result<(PackageConfig * Version * Consumer) list, Refusal> =
    let selected = selectConsumers config plan

    let refuse pkg version consumer reason logPath workspace =
        Error
            {
                Package = pkg
                Version = version
                Consumer = consumer
                Reason = reason
                LogPath = logPath
                Workspace = workspace
            }

    let candidates =
        selected
        |> List.map (fun (pkg, version, _) -> pkg.Name, version)
        |> List.distinct

    let packed =
        candidates
        |> List.fold
            (fun acc (name, version) ->
                acc
                |> Result.bind (fun () ->
                    let pkg = plan |> List.pick (fun (p, _) -> if p.Name = name then Some p else None)
                    printfn "Consumer canary: packing %s %s into %s" pkg.Name (format version) config.LocalFeed

                    packCandidate settings rootDir config.LocalFeed pkg version
                    |> Result.mapError (fun reason ->
                        let consumer =
                            selected |> List.pick (fun (p, _, c) -> if p.Name = name then Some c else None)

                        pkg, version, consumer, reason)))
            (Ok())

    let outcome =
        match packed with
        | Error(pkg, version, consumer, reason) -> refuse pkg version consumer reason None None
        | Ok() ->
            selected
            |> List.fold
                (fun acc (pkg, version, consumer) ->
                    acc
                    |> Result.bind (fun passed ->
                        let logPath = logPathFor settings.LogDir pkg version consumer
                        Directory.CreateDirectory settings.LogDir |> ignore

                        if File.Exists logPath then
                            File.Delete logPath

                        printfn
                            "Consumer canary: %s %s -> %s (gate: %s, up to %gm, log: %s)"
                            pkg.Name
                            (format version)
                            consumer.Repo
                            consumer.Gate
                            consumer.Timeout.TotalMinutes
                            logPath

                        match createWorkspace settings consumer logPath with
                        | Error reason -> refuse pkg version consumer reason (Some logPath) None
                        | Ok workspace ->
                            match runOne settings config.LocalFeed pkg version consumer workspace logPath with
                            | Ok() ->
                                printfn "Consumer canary: %s passed for %s %s" consumer.Repo pkg.Name (format version)
                                Ok(passed @ [ pkg, version, consumer ])
                            | Error reason -> refuse pkg version consumer reason (Some logPath) (Some workspace)))
                (Ok [])

    for name, version in candidates do
        evict settings name version

    outcome

let private formatBudget (budget: TimeSpan) : string =
    sprintf "%dm%ds" (int budget.TotalMinutes) budget.Seconds

let formatRefusal (refusal: Refusal) : string =
    let what =
        match refusal.Reason with
        | PackFailed error -> sprintf "packing the candidate failed: %s" error
        | WorkspaceFailed error -> sprintf "creating the workspace failed: %s" error
        | PinFailed error -> sprintf "pinning the candidate failed: %s" error
        | RestoreFailed code -> sprintf "restoring the candidate exited %d" code
        | RestoreTimedOut budget -> sprintf "restoring the candidate exceeded its %s budget" (formatBudget budget)
        | GateFailed code -> sprintf "gate `%s` exited %d" refusal.Consumer.Gate code
        | GateTimedOut budget -> sprintf "gate `%s` exceeded its %s budget" refusal.Consumer.Gate (formatBudget budget)

    [
        yield
            sprintf
                "Consumer canary REFUSED the release: %s (%s %s): %s"
                refusal.Consumer.Repo
                refusal.Package.Name
                (format refusal.Version)
                what
        match refusal.LogPath with
        | Some path -> yield sprintf "  log: %s" path
        | None -> ()
        match refusal.Workspace with
        | Some path -> yield sprintf "  workspace: %s (left for inspection)" path
        | None -> ()
        yield "  Nothing was tagged or pushed."
        yield
            "  If the consumer's main is red on its own, confirm with its gate on an unpinned workspace, \
           then re-run with --skip-consumer-canary."
    ]
    |> String.concat "\n"

let formatVerdict (verdict: Verdict) : string =
    match verdict with
    | Skipped(NoConsumerConfig path) -> sprintf "Consumer canary: skipped (no %s on this machine)" path
    | Skipped(NoConsumersForPlan path) ->
        sprintf "Consumer canary: skipped (%s names no consumer of these packages)" path
    | Skipped BreakGlass -> "Consumer canary: SKIPPED by --skip-consumer-canary (break-glass; no consumer gate ran)"
    | Passed [] -> "Consumer canary: nothing to run"
    | Passed runs ->
        runs
        |> List.map (fun (pkg, version, consumer) -> sprintf "%s %s -> %s" pkg.Name (format version) consumer.Repo)
        |> String.concat ", "
        |> sprintf "Consumer canary: passed (%s)"

/// One line per consumer a dry run would exercise.
let describePlan (config: CanaryConfig) (plan: (PackageConfig * Version) list) : string list =
    selectConsumers config plan
    |> List.map (fun (pkg, version, consumer) ->
        sprintf
            "  %s %s -> %s (pin %s, gate `%s`, up to %gm)"
            pkg.Name
            (format version)
            consumer.Repo
            consumer.Pin
            consumer.Gate
            consumer.Timeout.TotalMinutes)

/// The release's decision: skip (and say why), pass, or refuse. A malformed
/// config is an `Error` even though nothing ran, because "no consumers" must
/// never be the reading of a typo.
let decide (settings: Settings) (rootDir: string) (plan: (PackageConfig * Version) list) : Result<Verdict, string> =
    if settings.Skip then
        printfn ""
        printfn "!!! CONSUMER CANARY SKIPPED: --skip-consumer-canary (break-glass) !!!"
        printfn "!!! No consumer gate will run on this release candidate.          !!!"
        Ok(Skipped BreakGlass)
    else
        loadConfig settings.ConfigPath
        |> Result.bind (fun outcome ->
            match outcome with
            | NoConfig path -> Ok(Skipped(NoConsumerConfig path))
            | Loaded config when (selectConsumers config plan).IsEmpty ->
                Ok(Skipped(NoConsumersForPlan settings.ConfigPath))
            | Loaded config ->
                run settings rootDir config plan
                |> Result.map Passed
                |> Result.mapError formatRefusal)

/// Logs for the releasing repo `rootDir`: `~/.fssemantictagger/logs/<repo name>`.
let defaultLogDir (rootDir: string) : string =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".fssemantictagger",
        "logs",
        Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar))
    )

/// The production seam: real processes, the user's config and NuGet cache.
let defaultSettings (skip: bool) (rootDir: string) : Settings =
    {
        ConfigPath = defaultConfigPath
        Skip = skip
        LogDir = defaultLogDir rootDir
        ScratchDir = Path.Combine(Path.GetTempPath(), "fssemantictagger-canary-seed")
        PackagesCache = defaultPackagesCache
        Ops = { RunIn = runIn; RunGate = runLogged }
    }

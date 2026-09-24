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
        /// The revision the fresh workspace is created from.
        Revision: string
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
    { RunIn: string -> string -> string -> CommandResult
      RunGate: string -> string -> TimeSpan -> string -> GateOutcome }

[<NoEquality; NoComparison>]
type Settings =
    {
        ConfigPath: string
        /// `--skip-consumer-canary`.
        Skip: bool
        /// Where each consumer's restore-and-gate log is written.
        LogDir: string
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
    { Package: PackageConfig
      Version: Version
      Consumer: Consumer
      Reason: RefusalReason
      LogPath: string option
      Workspace: string option }

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

    let optionalStr (name: string) (fallback: string) (el: JsonElement) : Result<string, string> =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Ok(v.GetString())
        | true, _ -> Error(sprintf "%s: \"%s\" must be a string" path name)
        | _ -> Ok fallback

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
                            optionalStr "revision" "main" el
                            |> Result.map (fun revision ->
                                { Package = package
                                  Repo = expandHome repo
                                  Pin = pin
                                  Gate = gate
                                  Timeout = timeout
                                  Revision = revision }))))))

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
                        { LocalFeed = expandHome feed
                          Consumers = List.rev consumers }))))

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
            {| package = c.Package
               repo = c.Repo
               pin = c.Pin
               gate = c.Gate
               timeoutMinutes = c.Timeout.TotalMinutes
               revision = c.Revision |})

    JsonSerializer.Serialize(
        {| localFeed = config.LocalFeed
           consumers = consumers |},
        JsonSerializerOptions(WriteIndented = true)
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

let private feedSourceKey = "fssemantictagger-consumer-canary"

/// The workspace's `nuget.config` with the local feed as a source: a new
/// minimal file, or the existing one with a source inserted before
/// `</packageSources>` so it survives a `<clear />`.
let withLocalFeed (existing: string option) (feed: string) : string =
    let add = sprintf "    <add key=\"%s\" value=\"%s\" />" feedSourceKey feed

    match existing with
    | None ->
        String.concat
            "\n"
            [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
              "<configuration>"
              "  <packageSources>"
              add
              "  </packageSources>"
              "</configuration>"
              "" ]
    | Some content ->
        let closing =
            content.IndexOf("</packageSources>", StringComparison.OrdinalIgnoreCase)

        if closing >= 0 then
            content.Substring(0, closing)
            + add.TrimStart()
            + "\n  "
            + content.Substring closing
        else
            let configEnd =
                content.LastIndexOf("</configuration>", StringComparison.OrdinalIgnoreCase)

            let block = "  <packageSources>\n" + add + "\n  </packageSources>\n"

            if configEnd >= 0 then
                content.Substring(0, configEnd) + block + content.Substring configEnd
            else
                content + "\n" + block

/// `<repo>-canary-<package>`, beside the consumer's checkout.
let workspacePathFor (consumer: Consumer) : string =
    let slug = consumer.Package.ToLowerInvariant().Replace('.', '-')
    consumer.Repo.TrimEnd(Path.DirectorySeparatorChar) + "-canary-" + slug

let private workspaceName (consumer: Consumer) : string =
    "canary-" + consumer.Package.ToLowerInvariant().Replace('.', '-')

let logPathFor (logDir: string) (pkg: PackageConfig) (version: Version) (consumer: Consumer) : string =
    let consumerName =
        Path.GetFileName(consumer.Repo.TrimEnd(Path.DirectorySeparatorChar))

    Path.Combine(logDir, sprintf "%s-%s-%s.log" pkg.Name (format version) consumerName)

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

    let cached =
        Path.Combine(settings.PackagesCache, pkg.Name.ToLowerInvariant(), versionText.ToLowerInvariant())

    if Directory.Exists cached then
        Directory.Delete(cached, true)

    let args =
        sprintf "pack %s -c Release -p:ReleaseBuild=true -p:Version=%s -o %s" pkg.Fsproj versionText feed

    match settings.Ops.RunIn rootDir "dotnet" args with
    | Success _ -> Ok()
    | Failure(error, code) -> Error(PackFailed(sprintf "dotnet %s exited %d: %s" args code error))

/// A fresh workspace at `Revision`, beside the consumer's checkout. A leftover
/// from an earlier run (kept for inspection) is replaced.
let private createWorkspace (ops: Ops) (consumer: Consumer) : Result<string, RefusalReason> =
    let path = workspacePathFor consumer
    let isJj = Directory.Exists(Path.Combine(consumer.Repo, ".jj"))

    let isGit =
        Directory.Exists(Path.Combine(consumer.Repo, ".git"))
        || File.Exists(Path.Combine(consumer.Repo, ".git"))

    let discardLeftover () =
        if isJj then
            ops.RunIn consumer.Repo "jj" (sprintf "workspace forget %s" (workspaceName consumer))
            |> ignore

        if isGit then
            ops.RunIn consumer.Repo "git" (sprintf "worktree remove --force %s" path)
            |> ignore

            ops.RunIn consumer.Repo "git" "worktree prune" |> ignore

        if Directory.Exists path then
            Directory.Delete(path, true)

    let add () =
        if isJj then
            ops.RunIn
                consumer.Repo
                "jj"
                (sprintf "workspace add --name %s -r %s %s" (workspaceName consumer) consumer.Revision path)
        elif isGit then
            ops.RunIn consumer.Repo "git" (sprintf "worktree add --detach %s %s" path consumer.Revision)
        else
            Failure(sprintf "%s is neither a jj nor a git repository" consumer.Repo, 1)

    if not (Directory.Exists consumer.Repo) then
        Error(WorkspaceFailed(sprintf "consumer repo %s does not exist" consumer.Repo))
    else
        discardLeftover ()

        match add () with
        | Success _ -> Ok path
        | Failure(error, _) -> Error(WorkspaceFailed error)

let private restoreCommand (pin: PinFile) : string =
    match pin with
    | ToolManifest path -> sprintf "dotnet tool restore --tool-manifest %s" path
    | MsBuildProject path -> sprintf "dotnet restore %s" path

/// Pin the candidate, point the workspace at the feed, restore, then gate.
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
            |> Result.map (fun edited ->
                File.WriteAllText(pinPath, edited)
                pin))
    |> Result.bind (fun pin ->
        let nugetConfig =
            [ "nuget.config"; "NuGet.Config"; "NuGet.config" ]
            |> List.map (fun name -> Path.Combine(workspace, name))
            |> List.tryFind File.Exists
            |> Option.defaultValue (Path.Combine(workspace, "nuget.config"))

        let existing =
            if File.Exists nugetConfig then
                Some(File.ReadAllText nugetConfig)
            else
                None

        File.WriteAllText(nugetConfig, withLocalFeed existing feed)

        match settings.Ops.RunGate workspace (restoreCommand pin) consumer.Timeout logPath with
        | Exited 0 -> Ok()
        | Exited code -> Error(RestoreFailed code)
        | TimedOut budget -> Error(RestoreTimedOut budget))
    |> Result.bind (fun () ->
        match settings.Ops.RunGate workspace consumer.Gate consumer.Timeout logPath with
        | Exited 0 -> Ok()
        | Exited code -> Error(GateFailed code)
        | TimedOut budget -> Error(GateTimedOut budget))

/// Run every consumer the plan selects, stopping at the first refusal. Each
/// package is packed once, however many consumers it has.
let run
    (settings: Settings)
    (rootDir: string)
    (config: CanaryConfig)
    (plan: (PackageConfig * Version) list)
    : Result<(PackageConfig * Version * Consumer) list, Refusal> =
    let selected = selectConsumers config plan

    let refuse pkg version consumer reason logPath workspace =
        Error
            { Package = pkg
              Version = version
              Consumer = consumer
              Reason = reason
              LogPath = logPath
              Workspace = workspace }

    let packed =
        selected
        |> List.map (fun (pkg, version, _) -> pkg.Name, version)
        |> List.distinct
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

                    match createWorkspace settings.Ops consumer with
                    | Error reason -> refuse pkg version consumer reason (Some logPath) None
                    | Ok workspace ->
                        match runOne settings config.LocalFeed pkg version consumer workspace logPath with
                        | Ok() ->
                            printfn "Consumer canary: %s passed for %s %s" consumer.Repo pkg.Name (format version)
                            Ok(passed @ [ pkg, version, consumer ])
                        | Error reason -> refuse pkg version consumer reason (Some logPath) (Some workspace)))
            (Ok [])

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

    [ yield
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
           then re-run with --skip-consumer-canary." ]
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
            | NoConfig path ->
                printfn "%s" (formatVerdict (Skipped(NoConsumerConfig path)))
                Ok(Skipped(NoConsumerConfig path))
            | Loaded config when (selectConsumers config plan).IsEmpty ->
                printfn "%s" (formatVerdict (Skipped(NoConsumersForPlan settings.ConfigPath)))
                Ok(Skipped(NoConsumersForPlan settings.ConfigPath))
            | Loaded config ->
                run settings rootDir config plan
                |> Result.map Passed
                |> Result.mapError formatRefusal)

/// The production seam: real processes, the user's config and NuGet cache.
let defaultSettings (skip: bool) (rootDir: string) : Settings =
    { ConfigPath = defaultConfigPath
      Skip = skip
      LogDir = Path.Combine(rootDir, "artifacts", "consumer-canary")
      PackagesCache = defaultPackagesCache
      Ops = { RunIn = runIn; RunGate = runLogged } }

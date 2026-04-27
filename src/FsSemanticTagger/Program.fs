module FsSemanticTagger.Program

open System.IO
open CommandTree

type ReleaseFlag =
    | [<CmdFlag(Description = "Build and pack locally instead of pushing tags for CI")>] Publish
    | [<CmdFlag(Description = "Preview version bumps without modifying files or creating tags")>] DryRun

type Command =
    | [<Cmd("Initialize semantic-tagger.json by scanning for packable .fsproj files")>] Init
    | [<Cmd("Print the public API signatures of a built DLL"); CmdArg("Path to built DLL")>] ExtractApi of dll: string
    | [<Cmd("Compare two DLLs and report breaking vs additive API changes");
        CmdArg("Old DLL path");
        CmdArg("New DLL path", FieldIndex = 1);
        CmdExample("before/MyLib.dll after/MyLib.dll")>] CheckApi of oldDll: string * newDll: string
    | [<Cmd("Bump version and tag based on API diff vs the last release")>] Release of ReleaseFlag list
    | [<Cmd("Start an alpha pre-release cycle (X.Y.Z-alpha.N)")>] Alpha of ReleaseFlag list
    | [<Cmd("Promote the current pre-release to beta (X.Y.Z-beta.N)")>] Beta of ReleaseFlag list
    | [<Cmd("Promote the current pre-release to release candidate (X.Y.Z-rc.N)")>] Rc of ReleaseFlag list
    | [<Cmd("Promote the current pre-release to a stable release (X.Y.Z)")>] Stable of ReleaseFlag list

let initCommand (rootDir: string) : Result<int, string> =
    let jsonPath = Path.Combine(rootDir, "semantic-tagger.json")

    if File.Exists(jsonPath) then
        printfn "semantic-tagger.json already exists. No changes made."
        Ok 0
    else
        let projects = Config.findPackableProjects rootDir

        if projects.IsEmpty then
            Error "No packable .fsproj files found. Each package needs a <PackageId> element."
        else
            let isMulti = projects.Length > 1

            let packages =
                projects
                |> List.map (fun (name, relativePath) ->
                    let fsprojFullPath = Path.Combine(rootDir, relativePath)
                    let dllPath = Path.GetRelativePath(rootDir, Config.deriveDllPath fsprojFullPath)

                    let tagPrefix = if isMulti then name.ToLowerInvariant() + "-v" else "v"

                    ({ Name = name
                       Fsproj = relativePath
                       DllPath = dllPath
                       TagPrefix = tagPrefix
                       FsProjsSharingSameTag = [] }
                    : Config.PackageConfig))

            let config: Config.ToolConfig =
                { Packages = packages
                  ReservedVersions = Set.empty
                  PreBuildCmds = []
                  RootDir = rootDir }

            File.WriteAllText(jsonPath, Config.toJson config)
            printfn "Created semantic-tagger.json with %d package(s):" projects.Length

            for (name, _) in projects do
                printfn "  - %s" name

            Ok 0

let internal releaseMode (flags: ReleaseFlag list) : Release.ReleaseMode =
    if flags |> List.contains DryRun then
        Release.DryRun
    elif flags |> List.contains Publish then
        Release.LocalPublish
    else
        Release.PushTags

let internal runReleaseWith
    (cwd: string)
    (run: string -> string -> Shell.CommandResult)
    (extractPreviousApi: string -> string -> Api.ApiSignature list option)
    (extractCurrentApi: string -> Api.ApiSignature list)
    (releaseCmd: Release.ReleaseCommand)
    (flags: ReleaseFlag list)
    : Result<int, string> =
    match Config.load cwd with
    | Error msg -> Error msg
    | Ok config ->
        Ok(
            Release.release
                { Run = run
                  Config = config
                  Command = releaseCmd
                  Mode = releaseMode flags
                  ExtractPreviousApi = extractPreviousApi
                  ExtractCurrentApi = extractCurrentApi
                  CiPollIntervalMs = 15000
                  CiMaxAttempts = 60 }
        )

let private runRelease (releaseCmd: Release.ReleaseCommand) (flags: ReleaseFlag list) : Result<int, string> =
    runReleaseWith
        (Directory.GetCurrentDirectory())
        Shell.run
        Api.extractFromNuGetCache
        Api.extractFromAssembly
        releaseCmd
        flags

let internal runCommandWith
    (releaseHandler: Release.ReleaseCommand -> ReleaseFlag list -> Result<int, string>)
    (cmd: Command)
    : Result<int, string> =
    match cmd with
    | Init -> initCommand (Directory.GetCurrentDirectory())
    | ExtractApi dll ->
        if not (File.Exists dll) then
            Error(sprintf "DLL not found: %s" dll)
        else
            let sigs = Api.extractFromAssembly dll

            for (Api.ApiSignature s) in sigs do
                printfn "%s" s

            Ok 0
    | CheckApi(oldDll, newDll) ->
        let oldApi = Api.extractFromAssembly oldDll
        let newApi = Api.extractFromAssembly newDll
        let change = Api.compare oldApi newApi

        match change with
        | Api.Breaking _ ->
            printfn "BREAKING changes detected:"

            for (Api.ApiSignature s) in Api.ApiChange.toList change |> List.truncate 10 do
                printfn "  - %s" s

            Ok 2
        | Api.Addition _ ->
            printfn "Non-breaking additions:"

            for (Api.ApiSignature s) in Api.ApiChange.toList change |> List.truncate 10 do
                printfn "  + %s" s

            Ok 1
        | Api.NoChange ->
            printfn "No API changes"
            Ok 0
    | Release opts -> releaseHandler Release.Auto opts
    | Alpha opts -> releaseHandler Release.StartAlpha opts
    | Beta opts -> releaseHandler Release.PromoteToBeta opts
    | Rc opts -> releaseHandler Release.PromoteToRC opts
    | Stable opts -> releaseHandler Release.PromoteToStable opts

let runCommand (cmd: Command) : Result<int, string> = runCommandWith runRelease cmd

let private subcommandExtras (path: string list) : string option =
    match path with
    | [ "init" ] ->
        Some
            """
Scans the current directory for .fsproj files with a <PackageId> element
and creates semantic-tagger.json with one entry per packable project. In
multi-package repos, each project gets its own tag prefix (<name>-v...);
single-package repos use plain v... tags. Skips silently if the file
already exists.
"""
    | [ "extract-api" ] ->
        Some
            """
Loads <dll> with reflection and prints one line per public API element
(types, functions, fields, attributes). Useful to inspect what
check-api will compare against.

Arguments:
  dll  path to a built assembly (e.g. bin/Release/net10.0/MyProj.dll)
"""
    | [ "check-api" ] ->
        Some
            """
Compares the public API of <old-dll> and <new-dll>. Exit codes:
  0  no API change
  1  additions only (minor bump)
  2  breaking changes (major bump)

Arguments:
  old-dll  path to the previous version's assembly
  new-dll  path to the current build's assembly
"""
    | [ "release" ]
    | [ "alpha" ]
    | [ "beta" ]
    | [ "rc" ]
    | [ "stable" ] ->
        Some
            """
Reads semantic-tagger.json, locates each package's prior release tag,
extracts the public API from the cached NuGet package for that version,
diffs it against the current build, and bumps the version accordingly.

Modes (one chosen per invocation):
  release  auto-pick patch / minor / major based on the API diff
  alpha    start a new alpha pre-release (X.Y.Z-alpha.N)
  beta     promote the current pre-release to beta
  rc       promote the current pre-release to release candidate
  stable   promote the current pre-release to a stable release

Flags:
  --dry-run  print what would happen without writing or tagging
  --publish  build & pack locally instead of pushing tags (CI normally
             reacts to the pushed tag and does the pack itself)
"""
    | _ -> None

let private rootHelpExtras =
    """
Config file (semantic-tagger.json), produced by 'init':
  {
    "Packages": [
      {
        "Name": "MyTool",
        "Fsproj": "src/MyTool/MyTool.fsproj",
        "DllPath": "src/MyTool/bin/Release/net10.0/MyTool.dll",
        "TagPrefix": "mytool-v",
        "FsProjsSharingSameTag": []
      }
    ],
    "ReservedVersions": [],
    "PreBuildCmds": []
  }

  - TagPrefix is "v" in single-package repos and "<name>-v" in
    multi-package repos.
  - FsProjsSharingSameTag lets multiple .fsproj files version together
    under one tag.
  - PreBuildCmds run before the build that produces DllPath.

Examples:
  fssemantictagger init
  fssemantictagger extract-api src/MyTool/bin/Release/net10.0/MyTool.dll
  fssemantictagger check-api old.dll new.dll
  fssemantictagger release --dry-run
  fssemantictagger alpha
  fssemantictagger stable

Run 'fssemantictagger <command> --help' for command-specific details.
"""

let private normalizeHelpFlags (argv: string array) : string array =
    argv |> Array.map (fun a -> if a = "-h" || a = "help" then "--help" else a)

let run (rawArgv: string array) : Result<int, string> =
    let argv = normalizeHelpFlags rawArgv

    let tree =
        CommandReflection.fromUnion<Command> "Semantic versioning with API change detection"

    let printHelp (path: string list) =
        printfn "%s" (CommandTree.helpForPath tree path "fssemantictagger")

        if List.isEmpty path then
            printfn "%s" rootHelpExtras
        else
            match subcommandExtras path with
            | Some extras -> printfn "%s" extras
            | None -> ()

    if Array.isEmpty argv then
        printHelp []
        Ok 0
    else
        match CommandTree.parse tree argv with
        | Ok cmd -> runCommand cmd
        | Error(HelpRequested path) ->
            printHelp path
            Ok 0
        | Error err -> Error(sprintf "%A" err)

[<EntryPoint>]
let main argv =
    match run argv with
    | Ok code -> code
    | Error msg ->
        eprintfn "%s" msg
        1

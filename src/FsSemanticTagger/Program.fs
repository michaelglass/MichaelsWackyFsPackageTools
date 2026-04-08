module FsSemanticTagger.Program

open System.IO
open CommandTree

type Command =
    | [<Cmd("Initialize semantic-tagger.json config")>] Init
    | [<Cmd("Extract public API signatures from a DLL")>] ExtractApi of dll: string
    | [<Cmd("Compare APIs between two DLLs")>] CheckApi of oldDll: string * newDll: string
    | [<Cmd("Auto release based on API changes")>] Release
    | [<Cmd("Start alpha pre-release cycle")>] Alpha
    | [<Cmd("Promote to beta")>] Beta
    | [<Cmd("Promote to release candidate")>] Rc
    | [<Cmd("Promote to stable release")>] Stable

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
                  PreBuildCmds = [] }

            File.WriteAllText(jsonPath, Config.toJson config)
            printfn "Created semantic-tagger.json with %d package(s):" projects.Length

            for (name, _) in projects do
                printfn "  - %s" name

            Ok 0

let private runRelease (releaseCmd: Release.ReleaseCommand) : Result<int, string> =
    let config = Config.load (Directory.GetCurrentDirectory())
    let extractPreviousApi = Api.extractFromNuGetCache
    let extractCurrentApi = Api.extractFromAssembly
    Ok(Release.release Shell.run config releaseCmd Release.GitHubActions extractPreviousApi extractCurrentApi)

let runCommand (cmd: Command) : Result<int, string> =
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
        | Api.Breaking removed ->
            printfn "BREAKING changes detected:"

            for (Api.ApiSignature s) in removed |> List.truncate 10 do
                printfn "  - %s" s

            Ok 2
        | Api.Addition added ->
            printfn "Non-breaking additions:"

            for (Api.ApiSignature s) in added |> List.truncate 10 do
                printfn "  + %s" s

            Ok 1
        | Api.NoChange ->
            printfn "No API changes"
            Ok 0
    | Release -> runRelease Release.Auto
    | Alpha -> runRelease Release.StartAlpha
    | Beta -> runRelease Release.PromoteToBeta
    | Rc -> runRelease Release.PromoteToRC
    | Stable -> runRelease Release.PromoteToStable

let run (argv: string array) : Result<int, string> =
    let tree =
        CommandReflection.fromUnion<Command> "Semantic versioning with API change detection"

    if
        Array.isEmpty argv
        || argv |> Array.exists (fun a -> a = "--help" || a = "-h" || a = "help")
    then
        printfn "%s" (CommandTree.helpFull tree "fssemantictagger")
        Ok 0
    else
        match CommandTree.parse tree argv with
        | Ok cmd -> runCommand cmd
        | Error(HelpRequested path) ->
            printfn "%s" (CommandTree.helpForPath tree path "fssemantictagger")
            Ok 0
        | Error err -> Error(sprintf "%A" err)

[<EntryPoint>]
let main argv =
    match run argv with
    | Ok code -> code
    | Error msg ->
        eprintfn "%s" msg
        1

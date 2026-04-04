module FsSemanticTagger.Program

open System.IO

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

                    let tagPrefix =
                        if isMulti then
                            name.ToLowerInvariant() + "-v"
                        else
                            "v"

                    ({ Name = name
                       Fsproj = relativePath
                       DllPath = dllPath
                       TagPrefix = tagPrefix
                       ExtraFsprojs = [] }: Config.PackageConfig))

            let config: Config.ToolConfig =
                { Packages = packages
                  ReservedVersions = Set.empty }

            File.WriteAllText(jsonPath, Config.toJson config)
            printfn "Created semantic-tagger.json with %d package(s):" projects.Length

            for (name, _) in projects do
                printfn "  - %s" name

            Ok 0

let run (argv: string array) : Result<int, string> =
    match argv with
    | [| "init" |] -> initCommand (System.IO.Directory.GetCurrentDirectory())
    | [| "extract-api"; dllPath |] ->
        if not (System.IO.File.Exists dllPath) then
            Error(sprintf "DLL not found: %s" dllPath)
        else
            let sigs = Api.extractFromAssembly dllPath

            for (Api.ApiSignature s) in sigs do
                printfn "%s" s

            Ok 0
    | args when args.Length >= 1 && args.[0] = "check-api" ->
        match args.[1..] with
        | [| oldDll; newDll |] ->
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
        | _ -> Error "Usage: fssemantictagger check-api <old.dll> <new.dll>"
    | args when args.Length >= 1 && args.[0] = "release" ->
        let cmd =
            match args |> Array.tryItem 1 with
            | Some "alpha" -> Some Release.StartAlpha
            | Some "beta" -> Some Release.PromoteToBeta
            | Some "rc" -> Some Release.PromoteToRC
            | Some "stable" -> Some Release.PromoteToStable
            | Some "auto"
            | None -> Some Release.Auto
            | Some _other -> None

        match cmd with
        | None ->
            let unknown = args |> Array.tryItem 1 |> Option.defaultValue ""
            Error(sprintf "Unknown release command: %s" unknown)
        | Some cmd ->
            let config = Config.load (System.IO.Directory.GetCurrentDirectory())

            let mode =
                if args |> Array.contains "--publish" then
                    Release.LocalPublish
                else
                    Release.GitHubActions

            Ok(Release.release Shell.run config cmd mode)
    | _ ->
        Error(
            "Usage: fssemantictagger <init|release|check-api|extract-api> [options]\n\
             \n\
             Commands:\n\
             \  init\n\
             \  release [auto|alpha|beta|rc|stable] [--publish]\n\
             \  check-api <old.dll> <new.dll>\n\
             \  extract-api <dll-path>"
        )

[<EntryPoint>]
let main argv =
    match run argv with
    | Ok code -> code
    | Error msg ->
        printfn "%s" msg
        1

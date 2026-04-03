module FsSemanticTagger.Program

let run (argv: string array) : Result<int, string> =
    match argv with
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
            "Usage: fssemantictagger <release|check-api|extract-api> [options]\n\
             \n\
             Commands:\n\
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

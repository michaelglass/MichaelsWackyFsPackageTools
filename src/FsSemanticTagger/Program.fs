module FsSemanticTagger.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "extract-api"; dllPath |] ->
        let sigs = Api.extractFromAssembly dllPath

        for (Api.ApiSignature s) in sigs do
            printfn "%s" s

        0
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

                2
            | Api.Addition added ->
                printfn "Non-breaking additions:"

                for (Api.ApiSignature s) in added |> List.truncate 10 do
                    printfn "  + %s" s

                1
            | Api.NoChange ->
                printfn "No API changes"
                0
        | _ ->
            printfn "Usage: fssemantictagger check-api <old.dll> <new.dll>"
            1
    | args when args.Length >= 1 && args.[0] = "release" ->
        let config = Config.load (System.IO.Directory.GetCurrentDirectory())

        let cmd =
            match args |> Array.tryItem 1 with
            | Some "alpha" -> Release.StartAlpha
            | Some "beta" -> Release.PromoteToBeta
            | Some "rc" -> Release.PromoteToRC
            | Some "stable" -> Release.PromoteToStable
            | Some "auto"
            | None -> Release.Auto
            | Some other ->
                printfn "Unknown command: %s" other
                exit 1

        let mode =
            if args |> Array.contains "--publish" then
                Release.LocalPublish
            else
                Release.GitHubActions

        Release.release config cmd mode
    | _ ->
        printfn "Usage: fssemantictagger <release|check-api|extract-api> [options]"
        printfn ""
        printfn "Commands:"
        printfn "  release [auto|alpha|beta|rc|stable] [--publish]"
        printfn "  check-api <old.dll> <new.dll>"
        printfn "  extract-api <dll-path>"
        1

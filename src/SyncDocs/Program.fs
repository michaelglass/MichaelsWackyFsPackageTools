module SyncDocs.Program

open SyncDocs.Sync

let run (argv: string array) (rootDir: string) : Result<int, string> =
    let checkResult =
        match argv with
        | [| "check" |] -> Ok true
        | [| "sync" |] -> Ok false
        | _ -> Error "Usage: syncdocs <sync|check>"

    match checkResult with
    | Error msg -> Error msg
    | Ok check ->
        let pairs, warnings = discoverPairsAndWarnings rootDir

        for w in warnings do
            printfn "  Warning: %s" w

        if pairs.IsEmpty then
            printfn "No README.md -> docs/ pairs found"
            Ok 0
        else
            let results =
                pairs
                |> List.map (fun (source, target) ->
                    let shortSource = System.IO.Path.GetRelativePath(rootDir, source)
                    let shortTarget = System.IO.Path.GetRelativePath(rootDir, target)
                    let result = syncPair check source target

                    match result with
                    | InSync -> printfn "  %s -> %s: in sync" shortSource shortTarget
                    | Updated -> printfn "  %s -> %s: updated" shortSource shortTarget
                    | OutOfSync -> printfn "  %s -> %s: OUT OF SYNC" shortSource shortTarget
                    | SourceMissing -> printfn "  %s: source missing (skipped)" shortSource
                    | TargetMissing -> printfn "  %s -> %s: target missing (skipped)" shortSource shortTarget

                    result)

            Ok(if results |> List.contains OutOfSync then 1 else 0)

[<EntryPoint>]
let main argv =
    match run argv (System.IO.Directory.GetCurrentDirectory()) with
    | Ok code -> code
    | Error msg ->
        printfn "%s" msg
        1

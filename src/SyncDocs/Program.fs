module SyncDocs.Program

open SyncDocs.Sync

let private warningMessage (warning: DiscoveryWarning) : string =
    match warning with
    | MissingTarget(name, path) -> sprintf "Target docs file missing for %s, create %s" name path
    | MissingSource(name, path) -> sprintf "Source README missing for %s, create %s" name path

let run (argv: string array) (rootDir: string) : Result<int, string> =
    let modeResult =
        match argv with
        | [| "check" |] -> Ok Check
        | [| "sync" |] -> Ok Apply
        | _ -> Error "Usage: syncdocs <sync|check>"

    match modeResult with
    | Error msg -> Error msg
    | Ok mode ->
        let discovery = discoverPairsAndWarnings rootDir

        for w in discovery.Warnings do
            printfn "  Warning: %s" (warningMessage w)

        if discovery.Pairs.IsEmpty then
            printfn "No README.md -> docs/ pairs found"
            Ok 0
        else
            let results =
                discovery.Pairs
                |> List.map (fun pair ->
                    let shortSource = System.IO.Path.GetRelativePath(rootDir, pair.Source)
                    let shortTarget = System.IO.Path.GetRelativePath(rootDir, pair.Target)
                    let result = syncPair mode pair.Source pair.Target

                    match result with
                    | Ok InSync -> printfn "  %s -> %s: in sync" shortSource shortTarget
                    | Ok Updated -> printfn "  %s -> %s: updated" shortSource shortTarget
                    | Ok OutOfSync -> printfn "  %s -> %s: OUT OF SYNC" shortSource shortTarget
                    | Error(SourceMissing _) -> printfn "  %s: source missing (skipped)" shortSource
                    | Error(TargetMissing _) -> printfn "  %s -> %s: target missing (skipped)" shortSource shortTarget

                    result)

            let hasFailure =
                results
                |> List.exists (fun r ->
                    match r with
                    | Ok OutOfSync -> true
                    | Error _ -> true
                    | _ -> false)

            Ok(if hasFailure then 1 else 0)

[<EntryPoint>]
let main argv =
    match run argv (System.IO.Directory.GetCurrentDirectory()) with
    | Ok code -> code
    | Error msg ->
        printfn "%s" msg
        1

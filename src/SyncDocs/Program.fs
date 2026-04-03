module SyncDocs.Program

open SyncDocs.Sync

[<EntryPoint>]
let main argv =
    let check =
        match argv with
        | [| "check" |] -> true
        | [| "sync" |] -> false
        | _ ->
            printfn "Usage: syncdocs <sync|check>"
            exit 1

    let pairs = discoverPairs (System.IO.Directory.GetCurrentDirectory())

    if pairs.IsEmpty then
        printfn "No README.md -> docs/ pairs found"
        0
    else
        let mutable exitCode = 0

        for (source, target) in pairs do
            let shortSource =
                System.IO.Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), source)

            let shortTarget =
                System.IO.Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), target)

            match syncPair check source target with
            | InSync -> printfn "  %s -> %s: in sync" shortSource shortTarget
            | Updated -> printfn "  %s -> %s: updated" shortSource shortTarget
            | OutOfSync ->
                printfn "  %s -> %s: OUT OF SYNC" shortSource shortTarget
                exitCode <- 1
            | SourceMissing -> printfn "  %s: source missing (skipped)" shortSource
            | TargetMissing -> printfn "  %s -> %s: target missing (skipped)" shortSource shortTarget

        exitCode

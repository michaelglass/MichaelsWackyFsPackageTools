module SyncDocs.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "sync" |] ->
        printfn "sync: not yet implemented"
        1
    | [| "check" |] ->
        printfn "check: not yet implemented"
        1
    | _ ->
        printfn "Usage: syncdocs <sync|check>"
        1

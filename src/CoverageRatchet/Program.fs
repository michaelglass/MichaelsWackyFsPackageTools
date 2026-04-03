module CoverageRatchet.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "check" |] ->
        printfn "check: not yet implemented"
        1
    | [| "ratchet" |] ->
        printfn "ratchet: not yet implemented"
        1
    | _ ->
        printfn "Usage: coverageratchet <check|ratchet> [--config path]"
        1

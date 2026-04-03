module FsSemanticTagger.Program

[<EntryPoint>]
let main argv =
    match argv with
    | [| "extract-api"; _ |] -> printfn "extract-api: not yet implemented"; 1
    | [| "check-api" |] -> printfn "check-api: not yet implemented"; 1
    | [| "release" |] -> printfn "release: not yet implemented"; 1
    | _ -> printfn "Usage: fssemantictagger <release|check-api|extract-api> [options]"; 1

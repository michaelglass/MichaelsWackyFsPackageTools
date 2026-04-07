module FsProjLint.Program

open System.IO
open CommandTree
open FsProjLint.Checks

type Command = | [<Cmd("Check repo and project files for OSS readiness"); CmdDefault>] Check

let tree = CommandReflection.fromUnion<Command> "fsprojlint"

[<EntryPoint>]
let main argv =
    match CommandTree.parse tree argv with
    | Ok Check ->
        let result = runLint (Directory.GetCurrentDirectory())
        let allChecks = result.RepoChecks @ (result.ProjectChecks |> List.collect snd)
        let passed = allChecks |> List.filter (fun c -> c.Passed)
        let failed = allChecks |> List.filter (fun c -> not c.Passed)

        if not (List.isEmpty failed) then
            printfn "FAILED:"

            for c in failed do
                printfn "  FAIL %s" c.Name

        if not (List.isEmpty passed) then
            printfn "Passed:"

            for c in passed do
                printfn "  PASS %s" c.Name

        printfn "\nResult: %d/%d checks passed" passed.Length allChecks.Length

        if List.isEmpty failed then 0 else 1
    | Error(HelpRequested _) ->
        printfn "%s" (CommandTree.helpFull tree "fsprojlint")
        0
    | Error e ->
        eprintfn "%A" e
        1

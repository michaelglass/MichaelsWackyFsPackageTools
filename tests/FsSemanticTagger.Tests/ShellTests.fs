module FsSemanticTagger.Tests.ShellTests

open Xunit
open Tests.Common
open Swensen.Unquote
open FsSemanticTagger.Shell

[<Fact>]
let ``run - returns Success for successful command`` () =
    let result = run "echo" "hello"
    test <@ result = Success "hello" @>

[<Fact>]
let ``run - returns Failure for failing command`` () =
    match run "ls" "/nonexistent_path_xyz_abc_123" with
    | Failure _ -> ()
    | Success _ -> failwith "Expected Failure for nonexistent path"

[<Fact>]
let ``runOrFail - returns output for successful command`` () =
    let output = runOrFail "echo" "hello"
    test <@ output = "hello" @>

[<Fact>]
let ``runOrFail - throws for failing command`` () =
    let ex =
        Assert.Throws<System.Exception>(fun () -> runOrFail "ls" "/nonexistent_path_xyz_abc_123" |> ignore)

    test <@ ex.Message.Contains("failed") @>

[<Fact>]
let ``runSilent - returns Some for successful command`` () =
    let result = runSilent "echo" "hello"
    test <@ result = Some "hello" @>

[<Fact>]
let ``runSilent - returns None for failing command`` () =
    let result = runSilent "ls" "/nonexistent_path_xyz_abc_123"
    test <@ result = None @>

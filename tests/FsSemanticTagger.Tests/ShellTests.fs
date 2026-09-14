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

[<Fact>]
let ``run - returns stdout in Failure when stderr is empty`` () =
    // `sh`, not `bash`, and the difference matters only on Windows. GitHub's Windows
    // runners ship Git Bash on PATH, but the System32 directory comes earlier and holds
    // its own bash.exe — the WSL launcher. On a runner with no distro installed that prints
    // "Windows Subsystem for Linux has no installed distributions" in UTF-16 and exits
    // non-zero, so the assertion compared that against "stdout-only-error". `sh` has no
    // System32 twin and resolves to Git Bash, which is what the next test already relies
    // on. Nothing about the behaviour under test needs bash specifically.
    match run "sh" "-c \"printf 'stdout-only-error'; exit 1\"" with
    | Failure(msg, _) -> test <@ msg = "stdout-only-error" @>
    | Success _ -> failwith "Expected Failure"

[<Fact>]
let ``run - carries the process exit code in Failure`` () =
    match run "sh" "-c \"exit 3\"" with
    | Failure(_, exitCode) -> test <@ exitCode = 3 @>
    | Success _ -> failwith "Expected Failure"

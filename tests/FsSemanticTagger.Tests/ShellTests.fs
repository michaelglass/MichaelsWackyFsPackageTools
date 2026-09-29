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
    // `sh`, not `bash`: on Windows `bash` resolves to System32's WSL launcher.
    match run "sh" "-c \"printf 'stdout-only-error'; exit 1\"" with
    | Failure(msg, _) -> test <@ msg = "stdout-only-error" @>
    | Success _ -> failwith "Expected Failure"

[<Fact>]
let ``run - carries the process exit code in Failure`` () =
    match run "sh" "-c \"exit 3\"" with
    | Failure(_, exitCode) -> test <@ exitCode = 3 @>
    | Success _ -> failwith "Expected Failure"

[<Fact>]
let ``environmentFor - points git and gh at the store and nothing else`` () =
    let store = Some "/repo/.jj/repo/store/git"

    test <@ Shared.GitStoreEnvironment.environmentFor store "git" = [ "GIT_DIR", "/repo/.jj/repo/store/git" ] @>
    test <@ Shared.GitStoreEnvironment.environmentFor store "gh" = [ "GIT_DIR", "/repo/.jj/repo/store/git" ] @>
    test <@ List.isEmpty (Shared.GitStoreEnvironment.environmentFor store "jj") @>
    test <@ List.isEmpty (Shared.GitStoreEnvironment.environmentFor None "git") @>

[<Fact>]
let ``runWithGitDir - git reaches the store through its own environment, not this process's`` () =
    TestHelpers.withTempDir (fun tmpDir ->
        let store = System.IO.Path.Combine(tmpDir, "store")

        let gitIn args =
            run "git" (sprintf "-C \"%s\" %s" tmpDir args)

        test <@ gitIn "init -q --bare store" = Success "" @>
        test <@ gitIn "--git-dir=store config fsst.marker from-the-store" = Success "" @>
        let before = System.Environment.GetEnvironmentVariable "GIT_DIR"

        let answer = runWithGitDir (Some store) "git" "config --get fsst.marker"

        test <@ answer = Success "from-the-store" @>
        test <@ System.Environment.GetEnvironmentVariable "GIT_DIR" = before @>)

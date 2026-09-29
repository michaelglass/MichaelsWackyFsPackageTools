module CoverageRatchet.Tests.ShellTests

open Xunit
open Tests.Common
open Swensen.Unquote
open CoverageRatchet.Shell

[<Fact>]
let ``run - successful command returns Success with output`` () =
    let result = run "echo" "hello"

    test
        <@
            match result with
            | Success output -> output = "hello"
            | _ -> false
        @>

[<Fact>]
let ``run - successful command trims trailing whitespace`` () =
    // Shell.run accepts one command-line string, so preserve printf's
    // whitespace-bearing format as one quoted argument.
    let result = run "printf" "\"hello  \n\n\""

    test
        <@
            match result with
            | Success output -> output = "hello"
            | _ -> false
        @>

[<Fact>]
let ``run - failing command returns Failure with exit code`` () =
    let result = run "false" ""

    test
        <@
            match result with
            | Failure(_, exitCode) -> exitCode <> 0
            | _ -> false
        @>

[<Fact>]
let ``run - failing command with stderr returns stderr message`` () =
    let result = run "sh" "-c \"echo errormsg >&2; exit 1\""

    test
        <@
            match result with
            | Failure(msg, exitCode) -> msg.Contains("errormsg") && exitCode = 1
            | _ -> false
        @>

[<Fact>]
let ``run - failing command with only stdout returns stdout as message`` () =
    let result = run "sh" "-c \"echo stdoutmsg; exit 2\""

    test
        <@
            match result with
            | Failure(msg, exitCode) -> msg.Contains("stdoutmsg") && exitCode = 2
            | _ -> false
        @>

[<Fact>]
let ``runOrFail - successful command returns output`` () =
    let result = runOrFail "echo" "world"

    test <@ result = "world" @>

[<Fact>]
let ``runOrFail - failing command throws`` () = raises<exn> <@ runOrFail "false" "" @>

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

module CoverageRatchet.Tests.ShellTests

open Xunit
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
    let result = run "printf" "hello  \n\n"

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
let ``runOrFail - failing command throws`` () =
    raises<exn> <@ runOrFail "false" "" @>

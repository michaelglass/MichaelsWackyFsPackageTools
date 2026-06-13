module FsProjLint.Tests.GitignoreLeakTests

open System.Diagnostics
open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers

let private isPassed (result: CheckResult) =
    match result.Outcome with
    | Passed -> true
    | Failed _ -> false

let private isFailed (result: CheckResult) =
    match result.Outcome with
    | Passed -> false
    | Failed _ -> true

let private failureReason (result: CheckResult) =
    match result.Outcome with
    | Passed -> ""
    | Failed reason -> reason

/// Run a git command in `dir`, ignoring its output (used only to build fixtures).
let private git (dir: string) (args: string list) =
    let psi = ProcessStartInfo("git")
    psi.WorkingDirectory <- dir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    // Deterministic identity + no signing so commits work on any machine/CI.
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("user.name=test")
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("user.email=test@example.com")
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("commit.gpgsign=false")

    for a in args do
        psi.ArgumentList.Add(a)

    use p = Process.Start(psi)
    p.WaitForExit()
    p.ExitCode

let private writeFile (dir: string) (relativePath: string) (content: string) =
    let full = Path.Combine(dir, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
    File.WriteAllText(full, content)

let private initRepo (dir: string) =
    git dir [ "init"; "-q"; "-b"; "main" ] |> ignore

let private commitAll (dir: string) (message: string) =
    git dir [ "add"; "-A" ] |> ignore
    git dir [ "commit"; "-q"; "-m"; message ] |> ignore

[<Fact>]
let ``passes when directory is not a git repo`` () =
    withTempDir (fun dir ->
        let result = checkGitignoreLeaks dir
        test <@ isPassed result @>)

[<Fact>]
let ``passes for a clean git repo with no gitignored files in history`` () =
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" "bin/\nobj/\n.secret\n"
        writeFile dir "README.md" "hello"
        writeFile dir "src/Main.fs" "let x = 1"
        commitAll dir "initial"

        let result = checkGitignoreLeaks dir
        test <@ isPassed result @>)

[<Fact>]
let ``fails when a gitignored file is currently tracked`` () =
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" ".secret\n"
        writeFile dir "README.md" "hello"
        // Force-add the ignored file so it is committed and stays tracked.
        writeFile dir ".secret" "token"
        git dir [ "add"; "README.md"; ".gitignore" ] |> ignore
        git dir [ "add"; "-f"; ".secret" ] |> ignore
        commitAll dir "leak"

        let result = checkGitignoreLeaks dir
        test <@ isFailed result @>
        test <@ (failureReason result).Contains ".secret" @>
        test <@ (failureReason result).Contains "currently tracked" @>)

[<Fact>]
let ``fails when a gitignored file is history-only (no longer tracked)`` () =
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" ".secret\n"
        writeFile dir "README.md" "hello"
        writeFile dir ".secret" "token"
        git dir [ "add"; "README.md"; ".gitignore" ] |> ignore
        git dir [ "add"; "-f"; ".secret" ] |> ignore
        commitAll dir "add secret"
        // Remove it from tracking in a later commit -> history-only leak.
        git dir [ "rm"; "-q"; ".secret" ] |> ignore
        commitAll dir "remove secret"

        let result = checkGitignoreLeaks dir
        test <@ isFailed result @>
        test <@ (failureReason result).Contains ".secret" @>
        test <@ (failureReason result).Contains "history-only" @>)

/// Run git against an explicit GIT_DIR + work-tree (used to populate a fake jj
/// store the way jj itself does — the real git history lives under
/// `<root>/.jj/repo/store/git`, while the work-tree is `<root>`).
let private gitStore (store: string) (workTree: string) (args: string list) =
    let psi = ProcessStartInfo("git")
    psi.WorkingDirectory <- workTree
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.ArgumentList.Add("--git-dir")
    psi.ArgumentList.Add(store)
    psi.ArgumentList.Add("--work-tree")
    psi.ArgumentList.Add(workTree)
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("user.name=test")
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("user.email=test@example.com")
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add("commit.gpgsign=false")

    for a in args do
        psi.ArgumentList.Add(a)

    use p = Process.Start(psi)
    p.WaitForExit()
    p.ExitCode

[<Fact>]
let ``resolves the jj store and fails on a leak in a jj-backed repo`` () =
    withTempDir (fun root ->
        // jj keeps the real git history under <root>/.jj/repo/store/git, with a
        // `.jj/repo` directory present so GitDir.resolveGitDir walks to it.
        let store = Path.Combine(root, ".jj", "repo", "store", "git")
        Directory.CreateDirectory(store) |> ignore
        gitStore store root [ "init"; "-q"; "-b"; "main" ] |> ignore

        writeFile root ".gitignore" ".secret\n"
        writeFile root "README.md" "hello"
        writeFile root ".secret" "token"
        gitStore store root [ "add"; "README.md"; ".gitignore" ] |> ignore
        gitStore store root [ "add"; "-f"; ".secret" ] |> ignore
        gitStore store root [ "commit"; "-q"; "-m"; "leak" ] |> ignore

        let result = checkGitignoreLeaks root
        test <@ isFailed result @>
        test <@ (failureReason result).Contains ".secret" @>)

[<Fact>]
let ``passes for a jj-backed repo with no leaks`` () =
    withTempDir (fun root ->
        let store = Path.Combine(root, ".jj", "repo", "store", "git")
        Directory.CreateDirectory(store) |> ignore
        gitStore store root [ "init"; "-q"; "-b"; "main" ] |> ignore

        writeFile root ".gitignore" ".secret\n"
        writeFile root "README.md" "hello"
        gitStore store root [ "add"; "README.md"; ".gitignore" ] |> ignore
        gitStore store root [ "commit"; "-q"; "-m"; "clean" ] |> ignore

        let result = checkGitignoreLeaks root
        test <@ isPassed result @>)

[<Fact>]
let ``passes for a git repo with no commits`` () =
    withTempDir (fun dir ->
        // An initialized repo with no history: nothing was ever added, so there
        // is nothing to scan and the check passes.
        initRepo dir
        writeFile dir ".gitignore" ".secret\n"

        let result = checkGitignoreLeaks dir
        test <@ isPassed result @>)

[<Fact>]
let ``truncates the leak listing when more than 20 files leak`` () =
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" "secrets/\n"
        writeFile dir "README.md" "hello"
        git dir [ "add"; "README.md"; ".gitignore" ] |> ignore

        for i in 1..25 do
            writeFile dir (sprintf "secrets/leak-%02d.txt" i) "x"

        git dir [ "add"; "-f"; "secrets" ] |> ignore
        commitAll dir "many leaks"

        let result = checkGitignoreLeaks dir
        test <@ isFailed result @>
        let reason = failureReason result
        test <@ reason.Contains "25 gitignored file(s)" @>
        test <@ reason.Contains "... and 5 more" @>)

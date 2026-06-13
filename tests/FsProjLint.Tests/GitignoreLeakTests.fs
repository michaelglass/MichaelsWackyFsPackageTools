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

let private deleteFile (dir: string) (relativePath: string) =
    let full = Path.Combine(dir, relativePath)

    if File.Exists(full) then
        File.Delete(full)

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

[<Fact>]
let ``passes when the leak lives only on a different branch (not current ancestry)`` () =
    // The new scoping: the check scans the CURRENT branch's ancestry only. A
    // gitignored file committed on an unrelated branch that is NOT an ancestor
    // of HEAD must NOT fail the gate on the branch we publish from.
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" ".secret\n"
        writeFile dir "README.md" "hello"
        commitAll dir "base"

        // Experiment branch off base, WITH a force-added leak.
        git dir [ "checkout"; "-q"; "-b"; "experiment" ] |> ignore
        writeFile dir ".secret" "token"
        git dir [ "add"; "-f"; ".secret" ] |> ignore
        commitAll dir "leak on experiment"

        // Back to main, advance it with clean work. The leak is on a sibling
        // branch, not in main's ancestry.
        git dir [ "checkout"; "-q"; "main" ] |> ignore
        writeFile dir "src/Main.fs" "let x = 1"
        commitAll dir "clean work on main"

        let result = checkGitignoreLeaks dir
        test <@ isPassed result @>)

[<Fact>]
let ``fails for a leak on the current branch even when other branches exist`` () =
    // Mirror of the above: the leak IS in the current branch's ancestry, so it
    // fails — having other (clean) branches around does not mask it.
    withTempDir (fun dir ->
        initRepo dir
        writeFile dir ".gitignore" ".secret\n"
        writeFile dir "README.md" "hello"
        commitAll dir "base"

        // A clean sibling branch that should be irrelevant.
        git dir [ "checkout"; "-q"; "-b"; "other" ] |> ignore
        writeFile dir "src/Other.fs" "let y = 2"
        commitAll dir "clean other"

        // Current branch (main) carries the leak.
        git dir [ "checkout"; "-q"; "main" ] |> ignore
        writeFile dir ".secret" "token"
        git dir [ "add"; "-f"; ".secret" ] |> ignore
        commitAll dir "leak on main"

        let result = checkGitignoreLeaks dir
        test <@ isFailed result @>
        test <@ (failureReason result).Contains ".secret" @>)

// --- jj-backed fixtures -----------------------------------------------------
//
// These use a REAL jj repo (`jj git init --no-colocate`, which lays down the
// `<root>/.jj/repo/store/git` directory that GitDir.resolveGitDir resolves to)
// so they exercise the jj-specific "current commit" resolution: the check asks
// `jj log -r @-` for the working-copy parent's commit_id rather than git HEAD
// (which is unreliable under jj). If jj is not installed the fixture cannot be
// built and the test is a no-op pass.

/// Run a jj command in `dir`. Returns exit code; -1 if jj is not installed.
let private jj (dir: string) (args: string list) : int =
    try
        let psi = ProcessStartInfo("jj")
        psi.WorkingDirectory <- dir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add(a)

        match Process.Start(psi) with
        | null -> -1
        | p ->
            p.StandardOutput.ReadToEnd() |> ignore
            p.StandardError.ReadToEnd() |> ignore
            p.WaitForExit()
            p.ExitCode
    with _ ->
        -1

/// Initialise a real, non-colocated jj repo with a deterministic identity.
/// Returns false (skip the test) if jj is unavailable.
let private initJjRepo (dir: string) : bool =
    if jj dir [ "git"; "init"; "--no-colocate" ] <> 0 then
        false
    else
        jj dir [ "config"; "set"; "--repo"; "user.name"; "test" ] |> ignore
        jj dir [ "config"; "set"; "--repo"; "user.email"; "test@example.com" ] |> ignore
        true

[<Fact>]
let ``resolves the jj store and fails on a leak on the current branch`` () =
    withTempDir (fun root ->
        if initJjRepo root then
            // jj respects .gitignore for auto-tracking, so to plant a leak we
            // commit the secret BEFORE adding the ignore rule, then add the rule
            // in a later commit. The secret is now in the @- ancestry but is
            // currently gitignored.
            writeFile root ".secret" "token"
            writeFile root "README.md" "hello"
            jj root [ "describe"; "-m"; "commit secret" ] |> ignore

            jj root [ "new"; "-m"; "add gitignore" ] |> ignore
            writeFile root ".gitignore" ".secret\n"
            jj root [ "describe"; "-m"; "add gitignore" ] |> ignore

            // Advance the working copy so @- is the gitignore commit (the branch
            // tip we'd publish), with @ an empty working copy on top.
            jj root [ "new"; "-m"; "wc" ] |> ignore

            let result = checkGitignoreLeaks root
            test <@ isFailed result @>
            test <@ (failureReason result).Contains ".secret" @>)

[<Fact>]
let ``passes for a clean jj-backed repo`` () =
    withTempDir (fun root ->
        if initJjRepo root then
            writeFile root ".gitignore" ".secret\n"
            writeFile root "README.md" "hello"
            jj root [ "describe"; "-m"; "clean" ] |> ignore
            jj root [ "new"; "-m"; "wc" ] |> ignore

            let result = checkGitignoreLeaks root
            test <@ isPassed result @>)

[<Fact>]
let ``passes for a jj repo when the leak lives only on a different branch`` () =
    // The jj counterpart of the cross-branch scoping test: a leak on a sibling
    // jj commit that is NOT an ancestor of @- must not fail the gate.
    withTempDir (fun root ->
        if initJjRepo root then
            writeFile root ".gitignore" ".secret\n"
            writeFile root "README.md" "hello"
            jj root [ "describe"; "-m"; "base" ] |> ignore
            jj root [ "bookmark"; "create"; "base"; "-r"; "@" ] |> ignore

            // Experiment branch off base, carrying the leak. Remove the ignore
            // on this branch so jj tracks .secret into the experiment commit.
            jj root [ "new"; "base"; "-m"; "experiment" ] |> ignore
            deleteFile root ".gitignore"
            writeFile root ".secret" "token"
            jj root [ "describe"; "-m"; "experiment" ] |> ignore
            jj root [ "bookmark"; "create"; "experiment"; "-r"; "@" ] |> ignore

            // Clean main-work branch off base (keeps the ignore, no secret).
            jj root [ "new"; "base"; "-m"; "mainwork" ] |> ignore
            writeFile root "src/Main.fs" "let x = 1"
            jj root [ "bookmark"; "create"; "mainwork"; "-r"; "@" ] |> ignore

            // Working copy on top of main-work -> @- is the clean branch tip.
            jj root [ "new"; "mainwork"; "-m"; "wc" ] |> ignore

            let result = checkGitignoreLeaks root
            test <@ isPassed result @>)

[<Fact>]
let ``passes for a git repo with no commits`` () =
    withTempDir (fun dir ->
        // An initialized repo with no history: nothing was ever added, so there
        // is nothing to scan and the check passes (unborn HEAD -> no current
        // commit resolves).
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

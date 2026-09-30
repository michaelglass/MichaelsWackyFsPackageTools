/// Git and fake-jj-store fixtures shared by the checks that read a repository.
module FsProjLint.Tests.GitFixtures

open System.Diagnostics
open System.IO

/// Run git in `workingDir` with `prefixArgs` before the identity config and
/// `args`. Returns the exit code; output is discarded (fixtures only).
let private runGit (workingDir: string) (prefixArgs: string list) (args: string list) : int =
    let psi = ProcessStartInfo("git")
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    // Deterministic identity + no signing so commits work on any machine/CI.
    for a in
        prefixArgs
        @ [
            "-c"
            "user.name=test"
            "-c"
            "user.email=test@example.com"
            "-c"
            "commit.gpgsign=false"
        ]
        @ args do
        psi.ArgumentList.Add(a)

    use p = Process.Start(psi)
    p.StandardOutput.ReadToEnd() |> ignore
    p.StandardError.ReadToEnd() |> ignore
    p.WaitForExit()
    p.ExitCode

/// Run a git command in `dir`.
let git (dir: string) (args: string list) : int = runGit dir [] args

/// Run git against an explicit fake jj store (`<root>/.jj/repo/store/git`) the
/// way jj itself does: history under the store, work-tree at the root.
let gitStore (store: string) (workTree: string) (args: string list) : int =
    runGit workTree [ "--git-dir"; store; "--work-tree"; workTree ] args

/// Lay down a jj-backed repo without the jj binary: a git store at
/// `<root>/.jj/repo/store/git` and no `.git` at the root, which is what
/// `Shared.GitDir.resolveGitDir` recognises. Returns the store path.
let initFakeJjStore (root: string) : string =
    let store = Path.Combine(root, ".jj", "repo", "store", "git")
    Directory.CreateDirectory(store) |> ignore
    gitStore store root [ "init"; "-q"; "-b"; "main" ] |> ignore
    store

let writeFile (dir: string) (relativePath: string) (content: string) =
    let full = Path.Combine(dir, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
    File.WriteAllText(full, content)

/// Linked into FsSemanticTagger.Tests and FsProjLint.Tests through
/// <Compile Include="../Shared/GitDirTests.fs" Link="GitDirTests.fs" /> items, the way
/// src/Shared/GitDir.fs is linked into both tools. Each tool compiles its own copy of
/// `Shared.GitDir`, and coverage is measured per assembly, so each copy needs these tests
/// run against it. They build the `.jj` layout as plain files, so they need no `jj` binary
/// and cover the same edges on every platform, CI's Linux runner included (AUTOMATION-1764).
module Shared.Tests.GitDirTests

open System.IO
open Xunit
open Swensen.Unquote
open Shared.GitDir
open Tests.Common.TestHelpers

[<Fact>]
let ``resolveGitDir - default jj checkout (.jj/repo is a dir) targets the jj git store`` () =
    withTempDir (fun tmpDir ->
        let jjGitDir = Path.Combine(tmpDir, ".jj", "repo", "store", "git")
        Directory.CreateDirectory(jjGitDir) |> ignore

        let result = resolveGitDir tmpDir

        test <@ result = Some(Path.GetFullPath jjGitDir) @>)

[<Fact>]
let ``resolveGitDir - secondary workspace (.jj/repo is a pointer file) targets the real jj git store`` () =
    withTempDir (fun tmpDir ->
        // Real repo: <tmp>/realrepo/.jj/repo/store/git is a directory.
        let realRepoStore = Path.Combine(tmpDir, "realrepo", ".jj", "repo", "store", "git")

        Directory.CreateDirectory(realRepoStore) |> ignore

        // Secondary workspace: <tmp>/ws/.jj/repo is a FILE containing the relative path
        // (resolved relative to <tmp>/ws/.jj/) to <tmp>/realrepo/.jj/repo.
        let wsJj = Path.Combine(tmpDir, "ws", ".jj")
        Directory.CreateDirectory(wsJj) |> ignore
        File.WriteAllText(Path.Combine(wsJj, "repo"), "../../realrepo/.jj/repo\n")

        let result = resolveGitDir (Path.Combine(tmpDir, "ws"))

        test <@ result = Some(Path.GetFullPath realRepoStore) @>)

[<Fact>]
let ``resolveGitDir - secondary workspace pointer file with an absolute path targets the real jj git store`` () =
    withTempDir (fun tmpDir ->
        let realRepoStore = Path.Combine(tmpDir, "realrepo", ".jj", "repo", "store", "git")
        Directory.CreateDirectory(realRepoStore) |> ignore

        // Pointer file holds an ABSOLUTE path (the rooted branch), not a relative one.
        let wsJj = Path.Combine(tmpDir, "ws", ".jj")
        Directory.CreateDirectory(wsJj) |> ignore
        let absoluteRepo = Path.GetFullPath(Path.Combine(tmpDir, "realrepo", ".jj", "repo"))
        File.WriteAllText(Path.Combine(wsJj, "repo"), absoluteRepo + "\n")

        let result = resolveGitDir (Path.Combine(tmpDir, "ws"))

        test <@ result = Some(Path.GetFullPath realRepoStore) @>)

[<Fact>]
let ``resolveGitDir - a workspace of a colocated repo targets the git directory its store points at`` () =
    // AUTOMATION-1764: a colocated repo keeps no `store/git`; its store names the checkout's
    // `.git` in `git_target`, relative to the store. A secondary workspace of it (remote-ci's
    // ci-fresh-*) must resolve there, not walk up to the parent and defer to git's discovery,
    // which finds the PARENT worktree (or nothing under GIT_CEILING_DIRECTORIES).
    withTempDir (fun tmpDir ->
        let dotGit = Path.Combine(tmpDir, ".git")
        Directory.CreateDirectory(dotGit) |> ignore
        let store = Path.Combine(tmpDir, ".jj", "repo", "store")
        Directory.CreateDirectory(store) |> ignore
        File.WriteAllText(Path.Combine(store, "git_target"), "../../../.git")

        let wsJj = Path.Combine(tmpDir, ".workspaces", "ws", ".jj")
        Directory.CreateDirectory(wsJj) |> ignore
        File.WriteAllText(Path.Combine(wsJj, "repo"), "../../../.jj/repo\n")

        let result = resolveGitDir (Path.Combine(tmpDir, ".workspaces", "ws"))

        test <@ result = Some(Path.GetFullPath dotGit) @>)

[<Fact>]
let ``resolveGitDir - an absolute git_target is taken as written`` () =
    withTempDir (fun tmpDir ->
        let dotGit = Path.Combine(tmpDir, "elsewhere", ".git")
        Directory.CreateDirectory(dotGit) |> ignore
        let repo = Path.Combine(tmpDir, "repo")
        let store = Path.Combine(repo, ".jj", "repo", "store")
        Directory.CreateDirectory(store) |> ignore
        File.WriteAllText(Path.Combine(store, "git_target"), Path.GetFullPath dotGit + "\n")

        test <@ resolveGitDir repo = Some(Path.GetFullPath dotGit) @>)

[<Fact>]
let ``resolveGitDir - a git_target naming no directory resolves to nothing there`` () =
    withTempDir (fun tmpDir ->
        let repo = Path.Combine(tmpDir, "repo")
        let store = Path.Combine(repo, ".jj", "repo", "store")
        Directory.CreateDirectory(store) |> ignore
        File.WriteAllText(Path.Combine(store, "git_target"), "../../../.git")

        test <@ resolveGitDir repo = None @>)

[<Fact>]
let ``resolveGitDir - a store with neither a git directory nor a git_target resolves to nothing there`` () =
    // A jj repo on a backend other than git (jj's local or test backends) keeps a store with
    // no `git` directory and no `git_target`: there is no git store to expose.
    withTempDir (fun tmpDir ->
        let repo = Path.Combine(tmpDir, "repo")
        Directory.CreateDirectory(Path.Combine(repo, ".jj", "repo", "store")) |> ignore

        test <@ resolveGitDir repo = None @>)

[<Fact>]
let ``resolveGitDir - native .git checkout returns None`` () =
    withTempDir (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        let result = resolveGitDir tmpDir

        test <@ result = None @>)

[<Fact>]
let ``resolveGitDir - no repo anywhere up to the filesystem root returns None`` () =
    withTempDir (fun tmpDir ->
        // Neither .git nor .jj in tmpDir or any ancestor: the walk reaches the
        // filesystem root (GetParent -> null) and returns None.
        let nested = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(nested) |> ignore

        let result = resolveGitDir nested

        test <@ result = None @>)

[<Fact>]
let ``resolveGitDir - walks up from a nested subdir to the jj git store`` () =
    withTempDir (fun tmpDir ->
        // Repo root has the jj store; resolve from a deeply nested subdir.
        let jjGitDir = Path.Combine(tmpDir, ".jj", "repo", "store", "git")
        Directory.CreateDirectory(jjGitDir) |> ignore

        let nested = Path.Combine(tmpDir, "coverage", "Project")
        Directory.CreateDirectory(nested) |> ignore

        let result = resolveGitDir nested

        test <@ result = Some(Path.GetFullPath jjGitDir) @>)

[<Fact>]
let ``resolveGitDir - walks up from a nested subdir and stops at a native git root`` () =
    withTempDir (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        let nested = Path.Combine(tmpDir, "src", "Tool")
        Directory.CreateDirectory(nested) |> ignore

        let result = resolveGitDir nested

        test <@ result = None @>)

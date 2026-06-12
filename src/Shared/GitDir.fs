/// Shared between the CoverageRatchet and FsSemanticTagger tools via linked
/// <Compile Include="../Shared/GitDir.fs" Link="GitDir.fs" /> items, so the two
/// tools cannot drift. It is intentionally NOT its own project: adding a project
/// would alter each tool's ProjectReference closure and change what
/// FsSemanticTagger bundles at release time.
module Shared.GitDir

open System.IO

/// Resolves the git store directory to expose as GIT_DIR for a jj-backed repo.
///
/// Starting at `startDir` and walking up to the filesystem root, the first
/// ancestor that is a repo root decides the result:
///   * a `.git` entry (dir or file) -> None: a native git checkout, let git's
///     own discovery handle GIT_DIR; do not override it.
///   * a `.jj` dir -> Some <store/git>: `<root>/.jj/repo` is either a directory
///     (default checkout) or a small ASCII FILE whose contents are a path
///     (usually relative to `<root>/.jj/`) pointing at the real repo dir
///     (secondary workspace created by `jj workspace add`).
/// If no ancestor is a repo root, the result is None.
///
/// Walking up means resolution works from any nested subdirectory (e.g. the
/// per-project `coverage/<Project>/` dir the coverage tasks cd into), not just
/// from the repo root. For a caller already at the root the behavior is
/// identical to a single-level check.
let internal resolveGitDir (startDir: string) : string option =
    let storeFor (root: string) : string option =
        // `<root>/.jj/repo` is either a directory (default checkout) or a small
        // ASCII FILE whose contents are a path (usually relative to
        // `<root>/.jj/`) pointing at the real repo dir (secondary workspace
        // created by `jj workspace add`).
        let jjDir = Path.Combine(root, ".jj")
        let jjRepo = Path.Combine(jjDir, "repo")

        let realRepo =
            if Directory.Exists(jjRepo) then
                Some jjRepo
            elif File.Exists(jjRepo) then
                let pointer = File.ReadAllText(jjRepo).Trim()

                if Path.IsPathRooted(pointer) then
                    Some pointer
                else
                    Some(Path.Combine(jjDir, pointer))
            else
                None

        realRepo
        |> Option.map (fun repo -> Path.GetFullPath(Path.Combine(repo, "store", "git")))
        |> Option.filter Directory.Exists

    let rec walk (dir: string) : string option =
        let dotGit = Path.Combine(dir, ".git")

        if Directory.Exists(dotGit) || File.Exists(dotGit) then
            // Native git checkout: let git's own discovery handle GIT_DIR.
            None
        else
            match storeFor dir with
            | Some store -> Some store
            | None ->
                match Directory.GetParent(dir) with
                | null -> None
                | parent -> walk parent.FullName

    walk (Path.GetFullPath(startDir))

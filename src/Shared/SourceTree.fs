/// Which directories a scan of a repository's source tree descends into.
///
/// Compiled into FsSemanticTagger, FsProjLint and SyncDocs through linked
/// <Compile Include="../Shared/SourceTree.fs" Link="SourceTree.fs" /> items, and
/// loaded by scripts/check-docs-index.fsx, so every scanner in this repository
/// applies the same rule.
module Shared.SourceTree

open System.IO

/// Build and pack output, generated docs, and vendored dependencies.
let private buildDirs = set [ "bin"; "obj"; "artifacts"; "output"; "node_modules" ]

/// Does a scan skip `dir`? It skips build and pack output (`bin`, `obj`,
/// `artifacts`, fsdocs' `output`) and vendored dependencies (`node_modules`),
/// every dot-directory (`.git`, `.jj`, `.workspaces`, `.fshw`, `.devenv`, …), and
/// every directory that is its own checkout: one with a `.jj` or `.git` entry,
/// such as a jj workspace or a git worktree nested inside the repository. Such a
/// directory holds a copy of the sources, not more of them.
let internal isSkippedDir (dir: string) : bool =
    let name = Path.GetFileName(Path.TrimEndingDirectorySeparator dir)

    name.StartsWith "."
    || buildDirs.Contains name
    || Directory.Exists(Path.Combine(dir, ".jj"))
    || Directory.Exists(Path.Combine(dir, ".git"))
    || File.Exists(Path.Combine(dir, ".git"))

/// The files under `rootDir` matching `pattern`, descending only into
/// directories `isSkippedDir` keeps. `rootDir` itself is always scanned, even
/// when it is a checkout root. Sorted by path.
let internal findFiles (rootDir: string) (pattern: string) : string list =
    let rec walk (dir: string) : string seq =
        seq {
            yield! Directory.EnumerateFiles(dir, pattern)

            for sub in Directory.EnumerateDirectories dir do
                if not (isSkippedDir sub) then
                    yield! walk sub
        }

    walk rootDir |> Seq.sort |> Seq.toList

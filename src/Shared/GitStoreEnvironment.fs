// CoverageRatchet (github.com/michaelglass/CoverageRatchet) keeps its own copy of this file;
// if the copies drift, consider sharing them via a Paket GitHub file dependency.

/// Used by FsSemanticTagger, the tool here that starts `git` and `gh` processes
/// against a jj checkout, via a linked
/// <Compile Include="../Shared/GitStoreEnvironment.fs" Link="GitStoreEnvironment.fs" />
/// item.
module Shared.GitStoreEnvironment

/// The variables a child process named `cmd` needs to reach the git store
/// `gitDir`: GIT_DIR for `git`, and for `gh`, which finds its repository by
/// running git. Nothing for any other command, and nothing when `gitDir` is None
/// (a native git checkout, whose own discovery works).
///
/// This is set on the child process, never on this one: a process-wide GIT_DIR is
/// inherited by every concurrently started git, including ones aimed at other
/// repositories, which then fail with "this operation must be run in a work tree".
let internal environmentFor (gitDir: string option) (cmd: string) : (string * string) list =
    match gitDir, cmd with
    | Some dir, ("git" | "gh") -> [ "GIT_DIR", dir ]
    | _ -> []

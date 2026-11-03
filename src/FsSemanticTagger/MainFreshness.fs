/// Keeping a release on `main@origin` while it runs.
///
/// A release ends by moving `main` to its version-bump commit, and jj refuses to
/// move a bookmark backwards or sideways. So a release whose base is not the tip
/// of `main@origin` — because it started behind, or because another agent landed
/// on main while it ran — cannot finish. Observed 2026-11-02: a commit landed
/// during a 25-minute consumer canary, the canary's result was thrown away, and
/// the release died with a stack trace and a stray bump commit.
///
/// Two checks keep that from happening: one BEFORE the expensive steps (CI wait,
/// canary), which refuses a stale base while refusing is cheap, and one AFTER the
/// canary, which either moves the release onto the new main or refuses with
/// nothing written.
module FsSemanticTagger.MainFreshness

open FsSemanticTagger.Shell
open FsSemanticTagger.Vcs

let private listCommits (commits: MainCommit list) : string =
    commits
    |> List.map (fun c -> sprintf "  %s %s" c.Id c.Summary)
    |> String.concat "\n"

/// Files that feed EVERY project's build wherever they sit: MSBuild imports
/// (`Directory.Build.props` and friends, any `.props`/`.targets`), the SDK pin and
/// the package sources. A change to one can change a released package without
/// touching its directory, so it counts as touching every package.
let isSharedBuildInput (path: string) : bool =
    let name = System.IO.Path.GetFileName(path: string)

    let named (expected: string) =
        System.String.Equals(name, expected, System.StringComparison.OrdinalIgnoreCase)

    let endsWith (suffix: string) =
        name.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)

    endsWith ".props"
    || endsWith ".targets"
    || named "global.json"
    || named "nuget.config"
    || named "paket.dependencies"
    || named "paket.lock"

/// The changed `paths` that touch the release: a shared build input, or a path at
/// or under an entry of `footprint` (repo-relative directories and files, forward
/// slashes). An entry of `""` or `"."` is the repo root and so covers every path.
let touchingRelease (footprint: string list) (paths: string list) : string list =
    let covers (entry: string) (path: string) =
        entry = "" || entry = "." || path = entry || path.StartsWith(entry + "/")

    paths
    |> List.map (fun p -> p.Replace('\\', '/'))
    |> List.filter (fun path ->
        isSharedBuildInput path
        || footprint |> List.exists (fun entry -> covers entry path))

/// BEFORE the CI wait and the canary: fetch, then refuse unless the release base
/// (`@-`) already contains `main@origin`. A base AHEAD of `main@origin` passes:
/// that is an unpushed release commit, which the CI precondition handles. A failed
/// fetch refuses too — the question this answers cannot be answered from stale
/// remote refs, and nothing has been spent yet.
let confirmBaseIsMain (run: string -> string -> CommandResult) : Result<unit, string> =
    match fetchMain run with
    | Error reason ->
        Error(
            sprintf
                "could not fetch to confirm the release base is main@origin (jj git fetch: %s). Nothing was written; re-run once the remote is reachable."
                reason
        )
    | Ok() ->
        match commitsOnMainMissingFrom run "@-" with
        | Error reason ->
            Error(
                sprintf
                    "could not compare the release base with main@origin (%s). Nothing was written; re-run the release."
                    reason
            )
        | Ok [] -> Ok()
        | Ok commits ->
            Error(
                sprintf
                    "the release base (@-) is not main@origin: main@origin has %d commit(s) it lacks:\n%s\nA version-bump commit on this base could never move main forward, so the CI wait and the consumer canary would be wasted. Nothing was written. Re-run from the new main: `jj new main@origin`, then the same release command."
                    commits.Length
                    (listCommits commits)
            )

/// How the release stands after the post-canary fetch, when it may continue.
type Reconciled =
    /// `main@origin` has not moved: the release base is still its tip.
    | Unmoved
    /// `main@origin` moved by `commits`, none touching the release, and the working
    /// copy now sits on the new `main@origin`.
    | MovedOntoMain of commits: MainCommit list

/// AFTER the canary, BEFORE the first write: has `main@origin` moved, and if so,
/// does the canary's verdict still hold?
///
/// It holds when every new commit leaves the released packages alone. The canary
/// exercised consumers against packages built from the release base; a commit that
/// touches none of those packages' sources, bundled dependencies, changelogs or
/// shared build inputs (`footprint`, `isSharedBuildInput`) builds byte-for-byte the
/// same packages, so re-running a 25-minute canary to learn the same verdict buys
/// nothing. The release then moves onto the new main, and the version-bump commit
/// lands on top of it. The bump commit's own CI still gates the tags.
///
/// Refused, with nothing written, when a new commit touches the release, when the
/// base has commits main lacks (moving would drop them), or when the remote cannot
/// be read.
let reconcileAfterCanary
    (run: string -> string -> CommandResult)
    (footprint: string list)
    : Result<Reconciled, string> =
    let refuse (why: string) =
        Error(
            sprintf
                "%s Nothing was written or pushed; re-run from the new main: `jj new main@origin`, then the same release command."
                why
        )

    match fetchMain run with
    | Error reason -> refuse (sprintf "could not fetch after the consumer canary (jj git fetch: %s)." reason)
    | Ok() ->
        match commitsOnMainMissingFrom run "@-" with
        | Error reason -> refuse (sprintf "could not compare the release base with main@origin (%s)." reason)
        | Ok [] -> Ok Unmoved
        | Ok commits ->
            let moved =
                sprintf
                    "main moved during the release: main@origin gained %d commit(s):\n%s"
                    commits.Length
                    (listCommits commits)

            match isAncestorOfMain run "@-", pathsChangedOnMainSince run "@-" with
            | Error reason, _
            | _, Error reason -> refuse (sprintf "%s\nCould not tell whether they touch the release (%s)." moved reason)
            | Ok false, _ ->
                refuse (
                    sprintf
                        "%s\nThe release base has commits main@origin does not, so moving onto main would drop them."
                        moved
                )
            | Ok true, Ok paths ->
                match touchingRelease footprint paths with
                | [] ->
                    match moveOntoMain run with
                    | Ok() -> Ok(MovedOntoMain commits)
                    | Error reason ->
                        refuse (
                            sprintf
                                "%s\nThey touch no released package, but `jj new main@origin` failed: %s"
                                moved
                                reason
                        )
                | touched ->
                    refuse (
                        sprintf
                            "%s\nThey change the released packages, so the canary's verdict does not cover what would ship:\n%s\n"
                            moved
                            (touched |> List.map (sprintf "  %s") |> String.concat "\n")
                    )

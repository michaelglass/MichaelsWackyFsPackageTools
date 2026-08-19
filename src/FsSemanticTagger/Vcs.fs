module FsSemanticTagger.Vcs

open FsSemanticTagger.Shell
open FsSemanticTagger.Version

let internal runOrFail (run: string -> string -> CommandResult) (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure(error, _) -> failwithf "%s %s failed: %s" cmd args error

let private runSilent (run: string -> string -> CommandResult) (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

let private splitLines (output: string) : string array =
    output.Split('\n')
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s <> "")

let hasUncommittedChanges (run: string -> string -> CommandResult) : bool =
    // Use `jj diff --summary` rather than parsing the English `jj status` banner:
    // its output is one line per changed path (`M`/`A`/`D ...`) and empty when the
    // working copy is clean, so the check is locale-independent and unaffected by
    // wording changes to the status template.
    match run "jj" "diff --summary" with
    | Success output -> output.Trim() <> ""
    | Failure _ -> true

let tagExists (run: string -> string -> CommandResult) (tag: string) : bool =
    match run "jj" (sprintf "tag list %s" tag) with
    | Success output -> output.Contains(tag)
    | Failure _ ->
        match run "git" (sprintf "tag -l %s" tag) with
        | Success output -> output.Trim() = tag
        | Failure _ -> false

/// All tags matching `prefix`, parsed and sorted newest-first as (tag, version)
/// pairs. The single newest tag is `getLatestTag`; the full list is the seam for
/// walking back past an orphan tag (one whose package never landed on the feed)
/// to the most recent release that is actually published.
let getSortedTags (run: string -> string -> CommandResult) (prefix: string) : (string * Version) list =
    let output =
        match runSilent run "jj" (sprintf "tag list \"glob:%s*\" -T \"name ++ \\\"\\n\\\"\"" prefix) with
        | Some output when output.Trim() <> "" -> output
        | _ ->
            match runSilent run "git" (sprintf "tag -l \"%s*\"" prefix) with
            | Some output -> output
            | None -> ""

    if output = "" then
        []
    else
        splitLines output
        |> Array.choose (fun tag ->
            let versionStr = tag.Substring(prefix.Length)
            tryParse versionStr |> Result.toOption |> Option.map (fun v -> (tag, v)))
        |> Array.sortByDescending (fun (_, v) -> sortKey v)
        |> Array.toList

let getLatestTag (run: string -> string -> CommandResult) (prefix: string) : string option =
    getSortedTags run prefix |> List.tryHead |> Option.map fst

/// Point `tag` at `revision`, CREATING it when it is new and MOVING it when it
/// already exists.
///
/// Moving has to be asked for explicitly in both backends, and the path that most
/// needs it is the one that was broken: resuming an orphan tag (a tag whose package
/// never reached the feed) means re-pointing a tag that by definition already
/// exists. Without `--allow-move` jj refuses with `Error: Refusing to move tag`,
/// and without `-f` git refuses with `tag already exists` — so a resume could only
/// ever fail. Both flags are unconditional: creating a fresh tag behaves identically
/// with them, so there is nothing to branch on.
let tagRevision (run: string -> string -> CommandResult) (tag: string) (revision: string) : unit =
    match run "jj" (sprintf "tag set --allow-move %s -r %s" tag revision) with
    | Success _ -> ()
    | Failure _ ->
        runOrFail run "git" (sprintf "tag -f -a %s -m \"%s\" %s" tag tag revision)
        |> ignore

let commitAndAdvanceMain (run: string -> string -> CommandResult) (message: string) : unit =
    runOrFail run "jj" (sprintf "commit -m \"%s\"" message) |> ignore
    runOrFail run "jj" "bookmark set main -r @-" |> ignore

let hasChangesSinceTag (run: string -> string -> CommandResult) (tag: string) (path: string) : bool =
    let args = sprintf "diff --from %s --to @ --summary \"glob:%s/**\"" tag path

    match run "jj" args with
    | Success output -> output.Trim() <> ""
    | Failure _ -> true

/// Commit descriptions between `tag` (exclusive) and `@`, restricted to commits
/// that touch any of `paths`. Feeds the `## Unreleased` changelog derivation: the
/// raw, full descriptions since the last release for a package's change closure.
///
/// jj-native first: `jj log -r "<tag>..@"` with a `\x1e` (record-separator)
/// delimited `description` template and the paths as positional filesets. The
/// half-open `<tag>..@` range excludes the tag commit itself, and a jj revset
/// yields each commit once so merges don't duplicate. Falls back to
/// `git log <tag>..HEAD --format=%B%x1e -- <paths>`. Identical descriptions are
/// de-duplicated (a squash/cherry-pick can repeat one) and blanks dropped;
/// newest-first order is preserved. Empty when there are no such commits, or
/// when neither VCS can answer.
let descriptionsSinceTag (run: string -> string -> CommandResult) (tag: string) (paths: string list) : string list =
    let splitRecords (output: string) : string list =
        output.Split('\u001e')
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
        |> Array.toList
        |> List.distinct

    let quoted = paths |> List.map (fun p -> sprintf "\"%s\"" p) |> String.concat " "

    let jjArgs =
        sprintf "log -r \"%s..@\" --no-graph -T \"description ++ \\\"\\x1e\\\"\" %s" tag quoted

    match run "jj" jjArgs with
    | Success output -> splitRecords output
    | Failure _ ->
        match run "git" (sprintf "log %s..HEAD --format=%%B%%x1e -- %s" tag quoted) with
        | Success output -> splitRecords output
        | Failure _ -> []

let getCurrentCommitSha (run: string -> string -> CommandResult) : string option =
    let nonEmpty s =
        let trimmed = (s: string).Trim()
        if trimmed = "" then None else Some trimmed

    match runSilent run "jj" "log -r @ --no-graph -T commit_id" with
    | Some sha when sha.Trim() <> "" -> nonEmpty sha
    | _ ->
        match runSilent run "git" "rev-parse HEAD" with
        | Some sha -> nonEmpty sha
        | None -> None

// Shared with CoverageRatchet via the linked Shared/GitDir.fs compile item;
// walks up from any nested subdir to the repo root.
let internal resolveGitDir (startDir: string) : string option = Shared.GitDir.resolveGitDir startDir

let private withJjGitDir (f: unit -> 'a) : 'a =
    let gitDir = resolveGitDir (System.IO.Directory.GetCurrentDirectory())

    match gitDir with
    | Some dir -> System.Environment.SetEnvironmentVariable("GIT_DIR", dir)
    | None -> ()

    try
        f ()
    finally
        match gitDir with
        | Some _ -> System.Environment.SetEnvironmentVariable("GIT_DIR", null)
        | None -> ()

type RunStatus =
    | Completed
    | InProgressStatus
    | Queued
    | OtherStatus of string

module RunStatus =
    let ofString (s: string) : RunStatus =
        match s with
        | "completed" -> Completed
        | "in_progress" -> InProgressStatus
        | "queued" -> Queued
        | other -> OtherStatus other

type RunConclusion =
    | SuccessConclusion
    | FailureConclusion
    | CancelledConclusion
    | PendingConclusion
    | OtherConclusion of string

module RunConclusion =
    let ofString (s: string) : RunConclusion =
        match s with
        | "success" -> SuccessConclusion
        | "failure" -> FailureConclusion
        | "cancelled" -> CancelledConclusion
        | "pending" -> PendingConclusion
        | "" -> PendingConclusion
        | other -> OtherConclusion other

type CiRunInfo =
    { Name: string
      Url: string
      Status: RunStatus
      Conclusion: RunConclusion }

type CiStatus =
    | Passed
    | Failed of CiRunInfo list
    | InProgress of CiRunInfo list
    | NoRuns
    | Unknown

let parseCiRuns (json: string) : CiRunInfo list =
    let doc = System.Text.Json.JsonDocument.Parse(json)

    [ for elem in doc.RootElement.EnumerateArray() do
          let conclusionStr =
              let prop = elem.GetProperty("conclusion")

              if prop.ValueKind = System.Text.Json.JsonValueKind.Null then
                  ""
              else
                  prop.GetString()

          { Name = elem.GetProperty("name").GetString()
            Url = elem.GetProperty("url").GetString()
            Status = RunStatus.ofString (elem.GetProperty("status").GetString())
            Conclusion = RunConclusion.ofString conclusionStr } ]

let checkCiStatusForSha (run: string -> string -> CommandResult) (sha: string) : CiStatus =
    let args = sprintf "run list --commit %s --json status,conclusion,name,url" sha

    withJjGitDir (fun () ->
        match run "gh" args with
        | Success output ->
            let runs = parseCiRuns output

            if runs.IsEmpty then
                NoRuns
            else
                let failed =
                    runs
                    |> List.filter (fun r ->
                        match r.Conclusion with
                        | FailureConclusion
                        | CancelledConclusion -> true
                        | _ -> false)

                if failed.Length > 0 then
                    Failed failed
                elif
                    runs
                    |> List.forall (fun r ->
                        match r.Status, r.Conclusion with
                        | Completed, SuccessConclusion -> true
                        | _ -> false)
                then
                    Passed
                else
                    InProgress runs
        | Failure _ -> Unknown)

let private checkCiForSha (run: string -> string -> CommandResult) (sha: string) : bool =
    match checkCiStatusForSha run sha with
    | Passed -> true
    | _ -> false

let getCiStatus (run: string -> string -> CommandResult) : CiStatus =
    match getCurrentCommitSha run with
    | None -> Unknown
    | Some sha ->
        match checkCiStatusForSha run sha with
        | NoRuns when not (hasUncommittedChanges run) ->
            // In jj, @ is always a new commit, so a clean working copy means CI ran
            // on the parent.
            match runSilent run "jj" "log -r @- --no-graph -T commit_id" with
            | Some parentSha when parentSha.Trim() <> "" -> checkCiStatusForSha run (parentSha.Trim())
            | _ -> NoRuns
        | status -> status

let isCiPassing (run: string -> string -> CommandResult) : bool =
    match getCiStatus run with
    | Passed -> true
    | _ -> false

/// Is the release commit present on the remote? This is the distinction the
/// fail-fast CI precondition turns on: a commit that isn't on the remote can
/// NEVER have a CI run (so we fail fast / offer `--push`), whereas a pushed
/// commit whose run simply hasn't registered or finished yet must be *waited
/// for*, not failed.
///
/// jj-native first: a commit is pushed iff it is an ancestor of some
/// remote-tracking bookmark, i.e. it lies within the pushed history
/// (`<sha> & ::(remote_bookmarks())` is non-empty). MIND THE DIRECTION: the
/// mirror-image `remote_bookmarks() & ::<sha>` is true of every local commit
/// built on pushed main, so it calls an unpushed release commit "pushed". On a
/// plain-git repo the `jj` call fails and we fall back to `git branch -r
/// --contains` (the same question, correctly). An indeterminate result is treated
/// as "not pushed", so the caller errs toward the actionable "push first" message
/// rather than waiting forever on a run that will never appear.
let isCommitPushed (run: string -> string -> CommandResult) (sha: string) : bool =
    let jjAnswer =
        runSilent run "jj" (sprintf "log -r \"%s & ::(remote_bookmarks())\" --no-graph -T commit_id" sha)
        |> Option.map (fun out -> out.Trim() <> "")

    match jjAnswer with
    | Some pushed -> pushed
    | None ->
        withJjGitDir (fun () ->
            match runSilent run "git" (sprintf "branch -r --contains %s" sha) with
            | Some out -> out.Trim() <> ""
            | None -> false)

/// The release-commit sha that CI must have run on. In jj, `@` is the working
/// copy (never itself the pushed/CI'd commit when clean) — the real commit is
/// `@-`. So when the working copy is clean we report the parent; otherwise the
/// current commit. Falls back to whatever `getCurrentCommitSha` yields when the
/// parent can't be read.
let releaseCommitSha (run: string -> string -> CommandResult) : string option =
    let parent =
        if hasUncommittedChanges run then
            None
        else
            match runSilent run "jj" "log -r @- --no-graph -T commit_id" with
            | Some p when p.Trim() <> "" -> Some(p.Trim())
            | _ -> None

    match parent with
    | Some p -> Some p
    | None -> getCurrentCommitSha run

let hasCoverageRatchet (run: string -> string -> CommandResult) : bool =
    match run "dotnet" "tool list" with
    | Success output -> output.Contains("coverageratchet")
    | Failure _ -> false

let pushMain (run: string -> string -> CommandResult) : unit = runOrFail run "jj" "git push" |> ignore

/// How many workflow runs GitHub has for `gitRef`. A TAG name is a valid ref here: a
/// tag-triggered run reports the tag as its head branch.
///
/// `None` means "could not find out" — `gh` missing, unauthenticated, rate-limited, or
/// output we cannot parse. Deliberately distinct from `Some 0` ("asked, and there are
/// none"), because only the latter is evidence about the release.
let internal runCountForRef (run: string -> string -> CommandResult) (gitRef: string) : int option =
    let args = sprintf "run list --branch %s --json name --limit 20" gitRef

    withJjGitDir (fun () ->
        match run "gh" args with
        | Success output ->
            try
                use doc = System.Text.Json.JsonDocument.Parse(output)

                if doc.RootElement.ValueKind = System.Text.Json.JsonValueKind.Array then
                    Some(doc.RootElement.GetArrayLength())
                else
                    None
            with _ ->
                None
        | Failure _ -> None)

/// Push one tag, retrying a few times before giving up.
///
/// A push can fail for reasons that have nothing to do with the release and clear on
/// their own. Observed live: a release aborted twice with `sign_and_send_pubkey: ...
/// communication with agent failed`, and the identical push succeeded minutes later
/// with nothing changed. Without a retry that transient becomes a half-finished
/// release — version-bump commit pushed, tags not — and recovering by hand is what
/// tempts you into a batch push that GitHub then ignores entirely.
let private pushOneTag (run: string -> string -> CommandResult) (attempts: int) (delayMs: int) (tag: string) : unit =
    let rec go attempt =
        match run "git" (sprintf "push origin %s" tag) with
        | Success _ -> ()
        | Failure(error, _) when attempt < attempts ->
            let firstLine =
                error.Split('\n')
                |> Array.map (fun s -> s.Trim())
                |> Array.tryFind (fun s -> s <> "")

            printfn
                "  push of %s failed (attempt %d of %d), retrying: %s"
                tag
                attempt
                attempts
                (defaultArg firstLine "no output")

            System.Threading.Thread.Sleep(delayMs)
            go (attempt + 1)
        | Failure(error, _) -> failwithf "git push origin %s failed after %d attempts: %s" tag attempts error

    go 1

/// Push each tag and CONFIRM each one actually triggered a workflow run. Returns the
/// tags it could NOT confirm.
///
/// Pushing separately is required: a batch push of several tags can leave GitHub
/// creating no push events at all, so the tags sit on the remote and nothing ever builds
/// them. Observed: seven tags in one push produced zero runs; the same seven pushed
/// singly produced seven.
///
/// The confirmation is the point of this function. "I pushed a tag" is not "a release is
/// happening", and the difference is invisible from here — the tags are on the remote
/// either way. An unaskable question counts as UNCONFIRMED: answering "all fine" because
/// the check itself could not run would be the same lie one level down.
let pushTagsAndConfirm
    (run: string -> string -> CommandResult)
    (attempts: int)
    (delayMs: int)
    (tags: string list)
    : string list =
    runOrFail run "jj" "git export" |> ignore

    withJjGitDir (fun () ->
        for tag in tags do
            pushOneTag run attempts delayMs tag)

    // Give GitHub a moment to register the events before asking about them.
    if not (List.isEmpty tags) then
        System.Threading.Thread.Sleep(delayMs)

    tags
    |> List.filter (fun tag ->
        match runCountForRef run tag with
        | Some n when n > 0 -> false
        | _ -> true)

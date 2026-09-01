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
    | Failure(jjError, _) ->
        // The fallback exists for a PLAIN-GIT repo, where `jj` is not a thing. In a
        // non-colocated jj checkout it can never succeed — there is no root `.git` —
        // so quoting it alone reports `fatal: not a git repository`: true, useless,
        // and pointing at the wrong VCS while the jj error that IS the diagnosis is
        // discarded. Report both, and let the reader tell which repo they are in.
        match run "git" (sprintf "tag -f -a %s -m \"%s\" %s" tag tag revision) with
        | Success _ -> ()
        | Failure(gitError, _) -> failwithf "cannot tag %s at %s\n  jj: %s\n  git: %s" tag revision jjError gitError

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
    | SkippedConclusion
    | NeutralConclusion
    | FailureConclusion
    | CancelledConclusion
    | PendingConclusion
    | OtherConclusion of string

module RunConclusion =
    let ofString (s: string) : RunConclusion =
        match s with
        | "success" -> SuccessConclusion
        | "skipped" -> SkippedConclusion
        | "neutral" -> NeutralConclusion
        | "failure" -> FailureConclusion
        | "cancelled" -> CancelledConclusion
        | "pending" -> PendingConclusion
        | "" -> PendingConclusion
        | other -> OtherConclusion other

type CiRunInfo =
    { RunId: string
      RunIdOrdinal: int64
      Attempt: int
      CreatedAt: System.DateTimeOffset
      WorkflowId: int64 option
      Name: string
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

          let runId =
              match elem.TryGetProperty("databaseId") with
              | true, value -> value.ToString()
              | false, _ -> "unknown"

          let runIdOrdinal =
              match elem.TryGetProperty("databaseId") with
              | true, value -> value.GetInt64()
              | false, _ -> 0L

          let attempt =
              match elem.TryGetProperty("attempt") with
              | true, value -> value.GetInt32()
              | false, _ -> 1

          let createdAt =
              match elem.TryGetProperty("createdAt") with
              | true, value -> System.DateTimeOffset.Parse(value.GetString())
              | false, _ -> System.DateTimeOffset.MinValue

          let workflowId =
              match elem.TryGetProperty("workflowDatabaseId") with
              | true, value -> Some(value.GetInt64())
              | false, _ -> None

          { RunId = runId
            RunIdOrdinal = runIdOrdinal
            Attempt = attempt
            CreatedAt = createdAt
            WorkflowId = workflowId
            Name = elem.GetProperty("name").GetString()
            Url = elem.GetProperty("url").GetString()
            Status = RunStatus.ofString (elem.GetProperty("status").GetString())
            Conclusion = RunConclusion.ofString conclusionStr } ]

let checkCiStatusForSha (run: string -> string -> CommandResult) (sha: string) : CiStatus =
    let args =
        sprintf
            "run list --workflow .github/workflows/ci.yml --commit %s --json status,conclusion,name,url,databaseId,attempt,createdAt,workflowDatabaseId"
            sha

    withJjGitDir (fun () ->
        match run "gh" args with
        | Success output ->
            // The workflow PATH is the stable identity. Display names are not:
            // two workflows may share one, and a name can be edited at any time.
            let consideredRuns = parseCiRuns output

            let ordered =
                consideredRuns
                |> List.sortByDescending (fun workflowRun ->
                    workflowRun.CreatedAt, workflowRun.Attempt, workflowRun.RunIdOrdinal)

            let runs = ordered |> List.truncate 1

            let reportRefusal reason =
                printfn "Required CI workflow refused the release for %s: %s" sha reason

                for workflowRun in consideredRuns do
                    let decision =
                        if runs |> List.contains workflowRun then
                            "authoritative attempt"
                        else
                            "older attempt"

                    printfn
                        "  run %s attempt %d workflow %A: %s — created=%O status=%A conclusion=%A — %s"
                        workflowRun.RunId
                        workflowRun.Attempt
                        workflowRun.WorkflowId
                        workflowRun.Name
                        workflowRun.CreatedAt
                        workflowRun.Status
                        workflowRun.Conclusion
                        decision

            if runs.IsEmpty then
                reportRefusal "no run from .github/workflows/ci.yml exists on the exact release SHA"
                NoRuns
            else
                let latest = List.head runs

                let workflowIdentityAmbiguous =
                    ordered |> List.choose _.WorkflowId |> List.distinct |> List.length > 1

                let ambiguous =
                    ordered
                    |> List.skip 1
                    |> List.exists (fun other ->
                        other.CreatedAt = latest.CreatedAt
                        && other.Attempt = latest.Attempt
                        && other.RunIdOrdinal = latest.RunIdOrdinal)

                if workflowIdentityAmbiguous then
                    reportRefusal "the workflow path resolved to more than one workflow database id"
                    Failed ordered
                elif ambiguous then
                    reportRefusal "multiple newest CI runs have the same creation time and attempt"

                    Failed(
                        ordered
                        |> List.takeWhile (fun other ->
                            other.CreatedAt = latest.CreatedAt
                            && other.Attempt = latest.Attempt
                            && other.RunIdOrdinal = latest.RunIdOrdinal)
                    )
                elif latest.Status = Completed && latest.Conclusion = SuccessConclusion then
                    Passed
                elif latest.Status = InProgressStatus || latest.Status = Queued then
                    reportRefusal "the newest required CI attempt is queued or running"
                    InProgress runs
                else
                    reportRefusal "the newest required CI attempt ended without success"
                    Failed runs
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
/// Why a push failed, in the operator's terms rather than git's.
///
/// AUTOMATION-309: raw `git push` against an HTTPS remote with no credential
/// helper fails with git's generic "Please make sure you have the correct access
/// rights and the repository exists." Both readings of that sentence were false
/// in the incident it describes — the SSH agent was loaded and answering, and the
/// account had access — and each cost real time to rule out. The sentence is
/// SSH-flavoured; the remote was HTTPS. Repeating it verbatim sends the operator
/// to the wrong place.
let internal diagnosePushFailure (run: string -> string -> CommandResult) (error: string) : string =
    let remote =
        match run "git" "remote get-url origin" with
        | Success url -> url.Trim()
        | Failure _ -> ""

    let helper =
        match run "git" "config --get-regexp ^credential" with
        | Success text when text.Trim() <> "" -> Some(text.Trim())
        | _ -> None

    let looksHttps =
        remote.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)

    match looksHttps, helper with
    | true, None ->
        sprintf
            "the remote is HTTPS (%s) and NO git credential helper is configured, so a raw `git push` \
             has no way to authenticate. This is not an SSH-key problem — an SSH agent is irrelevant to \
             a push that never uses SSH. Fix with `gh auth setup-git`, or point the remote at SSH. \
             git said: %s"
            remote
            (error.Trim())
    | true, Some h ->
        sprintf
            "the remote is HTTPS (%s) and a credential helper IS configured (%s), so the credential \
             itself is likely rejected or expired rather than absent. git said: %s"
            remote
            h
            (error.Trim())
    | false, _ -> sprintf "pushing to %s failed. git said: %s" (if remote = "" then "origin" else remote) (error.Trim())

/// Push one tag, preferring the repo's own VCS front-end.
///
/// `jj git push --tag` is tried FIRST and raw `git push` only as a fallback,
/// which is the ordering AUTOMATION-309 asks for: jj authenticates in exactly the
/// environment where the shelled-out git cannot, so preferring it removes the
/// credential question rather than diagnosing it. The fallback stays because a
/// non-jj checkout is still a supported place to run this.
///
/// Returns the failure rather than raising it. A push failure is an EXPECTED
/// condition — the remote can be unreachable, the credential can be missing — and
/// letting `failwith` escape to `main` aborted the process with SIGABRT after the
/// version-bump commit had already been pushed, which is how two release attempts
/// left in-tree versions ahead of the newest published tag with no tags to explain
/// it.
let private pushOneTag
    (run: string -> string -> CommandResult)
    (attempts: int)
    (delayMs: int)
    (tag: string)
    : Result<unit, string> =
    let attemptPush () =
        match run "jj" (sprintf "git push --tag %s" tag) with
        | Success _ -> Ok()
        | Failure(jjError, _) ->
            // Fall back to raw git: a checkout that is not a jj repo has no `jj`
            // to answer, and that is not a failure worth reporting as one.
            match run "git" (sprintf "push origin %s" tag) with
            | Success _ -> Ok()
            | Failure(gitError, _) ->
                Error(sprintf "%s (jj also refused: %s)" (diagnosePushFailure run gitError) (jjError.Trim()))

    let rec go attempt =
        match attemptPush () with
        | Ok() -> Ok()
        | Error reason when attempt < attempts ->
            let firstLine =
                reason.Split('\n')
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
        | Error reason -> Error(sprintf "push of %s failed after %d attempts: %s" tag attempts reason)

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
type internal TagConfirmationFailure =
    | PushFailed of tag: string * reason: string
    | WorkflowTriggerMissing of tag: string

let internal pushTagsAndConfirmDetailed
    (run: string -> string -> CommandResult)
    (attempts: int)
    (delayMs: int)
    (tags: string list)
    : TagConfirmationFailure list =
    runOrFail run "jj" "git export" |> ignore

    // A tag that never reached the remote is a DIFFERENT failure from one that
    // reached it and triggered nothing, and the operator's next move differs too:
    // one is "authenticate and retry", the other is "re-push to trigger". Both
    // end up in the returned list — silence about either would be the lie this
    // function exists to prevent — but a failed push says so in its own words.
    let failures =
        withJjGitDir (fun () ->
            tags
            |> List.choose (fun tag ->
                match pushOneTag run attempts delayMs tag with
                | Ok() -> None
                | Error reason -> Some(tag, reason)))

    // Give GitHub a moment to register the events before asking about them.
    if not (List.isEmpty tags) then
        System.Threading.Thread.Sleep(delayMs)

    let pushFailures = Map.ofList failures

    tags
    |> List.choose (fun tag ->
        // Never ASK about a tag we know did not land: `gh` would answer "no run",
        // which is true and would be reported under the wrong heading.
        match pushFailures.TryFind tag with
        | Some reason -> Some(PushFailed(tag, reason))
        | None ->
            match runCountForRef run tag with
            | Some n when n > 0 -> None
            | _ -> Some(WorkflowTriggerMissing tag))

/// Compatibility surface for callers that only need the tag names. Release uses
/// the detailed result so it cannot tell an operator that a failed push landed.
let pushTagsAndConfirm
    (run: string -> string -> CommandResult)
    (attempts: int)
    (delayMs: int)
    (tags: string list)
    : string list =
    pushTagsAndConfirmDetailed run attempts delayMs tags
    |> List.map (function
        | PushFailed(tag, reason) ->
            eprintfn "  %s" reason
            tag
        | WorkflowTriggerMissing tag -> tag)

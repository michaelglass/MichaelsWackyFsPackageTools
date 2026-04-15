module FsSemanticTagger.Vcs

open FsSemanticTagger.Shell
open FsSemanticTagger.Version

let internal runOrFail (run: string -> string -> CommandResult) (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure error -> failwithf "%s %s failed: %s" cmd args error

let private runSilent (run: string -> string -> CommandResult) (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

let private splitLines (output: string) : string array =
    output.Split('\n')
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s <> "")

let hasUncommittedChanges (run: string -> string -> CommandResult) : bool =
    match run "jj" "status" with
    | Success output ->
        not (
            output.Contains("The working copy is clean")
            || output.Contains("The working copy has no changes")
        )
    | Failure _ -> true

let tagExists (run: string -> string -> CommandResult) (tag: string) : bool =
    match run "jj" (sprintf "tag list %s" tag) with
    | Success output -> output.Contains(tag)
    | Failure _ ->
        match run "git" (sprintf "tag -l %s" tag) with
        | Success output -> output.Trim() = tag
        | Failure _ -> false

let getLatestTag (run: string -> string -> CommandResult) (prefix: string) : string option =
    // Try jj first, fall back to git
    let output =
        match runSilent run "jj" (sprintf "tag list \"glob:%s*\" -T \"name ++ \\\"\\n\\\"\"" prefix) with
        | Some output when output.Trim() <> "" -> output
        | _ ->
            match runSilent run "git" (sprintf "tag -l \"%s*\"" prefix) with
            | Some output -> output
            | None -> ""

    if output = "" then
        None
    else
        splitLines output
        |> Array.choose (fun tag ->
            let versionStr = tag.Substring(prefix.Length)
            tryParse versionStr |> Result.toOption |> Option.map (fun v -> (tag, v)))
        |> Array.sortByDescending (fun (_, v) -> sortKey v)
        |> Array.tryHead
        |> Option.map fst

let tagRevision (run: string -> string -> CommandResult) (tag: string) (revision: string) : unit =
    match run "jj" (sprintf "tag set %s -r %s" tag revision) with
    | Success _ -> ()
    | Failure _ ->
        runOrFail run "git" (sprintf "tag -a %s -m \"%s\" %s" tag tag revision)
        |> ignore

let commitAndAdvanceMain (run: string -> string -> CommandResult) (message: string) : unit =
    runOrFail run "jj" (sprintf "commit -m \"%s\"" message) |> ignore
    runOrFail run "jj" "bookmark set main -r @-" |> ignore

let hasChangesSinceTag (run: string -> string -> CommandResult) (tag: string) (path: string) : bool =
    let args = sprintf "diff --from %s --to @ \"glob:%s/**\"" tag path

    match run "jj" args with
    | Success output -> output.Trim() <> ""
    | Failure _ -> true

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

let internal resolveGitDir (repoRoot: string) : string option =
    let dotGit = System.IO.Path.Combine(repoRoot, ".git")

    if System.IO.Directory.Exists(dotGit) || System.IO.File.Exists(dotGit) then
        None
    else
        let jjGitDir = System.IO.Path.Combine(repoRoot, ".jj", "repo", "store", "git")

        if System.IO.Directory.Exists(jjGitDir) then
            Some(System.IO.Path.GetFullPath(jjGitDir))
        else
            None

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
            // In jj, @ is always a new commit. Check parent if working copy is clean.
            match runSilent run "jj" "log -r @- --no-graph -T commit_id" with
            | Some parentSha when parentSha.Trim() <> "" -> checkCiStatusForSha run (parentSha.Trim())
            | _ -> NoRuns
        | status -> status

let isCiPassing (run: string -> string -> CommandResult) : bool =
    match getCiStatus run with
    | Passed -> true
    | _ -> false

let hasCoverageRatchet (run: string -> string -> CommandResult) : bool =
    match run "dotnet" "tool list" with
    | Success output -> output.Contains("coverageratchet")
    | Failure _ -> false

let pushMain (run: string -> string -> CommandResult) : unit = runOrFail run "jj" "git push" |> ignore

let pushTags (run: string -> string -> CommandResult) (tags: string list) : unit =
    // Export jj tags to the underlying git repo
    runOrFail run "jj" "git export" |> ignore

    // Push each tag separately so each gets its own GitHub Actions push event
    withJjGitDir (fun () ->
        for tag in tags do
            runOrFail run "git" (sprintf "push origin %s" tag) |> ignore)

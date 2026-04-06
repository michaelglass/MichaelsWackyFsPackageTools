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

            try
                Some(tag, parse versionStr)
            with _ ->
                None)
        |> Array.sortByDescending (fun (_, v) -> sortKey v)
        |> Array.tryHead
        |> Option.map fst

let commitAndTag (run: string -> string -> CommandResult) (prefix: string) (version: Version) : string =
    let tag = toTag prefix version
    let msg = sprintf "Release %s" (format version)
    runOrFail run "jj" (sprintf "commit -m \"%s\"" msg) |> ignore

    // Try jj tag first, fall back to git tag
    match run "jj" (sprintf "tag set %s" tag) with
    | Success _ -> ()
    | Failure _ -> runOrFail run "git" (sprintf "tag -a %s -m \"%s\"" tag msg) |> ignore

    tag

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

let private withJjGitDir (f: unit -> 'a) : 'a =
    let gitDir = System.IO.Path.Combine(".jj", "repo", "store", "git")
    let needsGitDir = System.IO.Directory.Exists(gitDir)

    if needsGitDir then
        System.Environment.SetEnvironmentVariable("GIT_DIR", gitDir)

    try
        f ()
    finally
        if needsGitDir then
            System.Environment.SetEnvironmentVariable("GIT_DIR", null)

type CiRunInfo =
    { Name: string
      Url: string
      Status: string
      Conclusion: string }

type CiStatus =
    | Passed
    | Failed of CiRunInfo list
    | InProgress of CiRunInfo list
    | NoRuns
    | Unknown

let parseCiRuns (json: string) : CiRunInfo list =
    let doc = System.Text.Json.JsonDocument.Parse(json)

    [ for elem in doc.RootElement.EnumerateArray() do
          { Name = elem.GetProperty("name").GetString()
            Url = elem.GetProperty("url").GetString()
            Status = elem.GetProperty("status").GetString()
            Conclusion =
              let prop = elem.GetProperty("conclusion")

              if prop.ValueKind = System.Text.Json.JsonValueKind.Null then
                  ""
              else
                  prop.GetString() } ]

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
                    |> List.filter (fun r -> r.Conclusion = "failure" || r.Conclusion = "cancelled")

                if failed.Length > 0 then
                    Failed failed
                elif
                    runs
                    |> List.forall (fun r -> r.Status = "completed" && r.Conclusion = "success")
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

let pushTags (run: string -> string -> CommandResult) (tags: string list) : unit =
    runOrFail run "jj" "git export" |> ignore

    withJjGitDir (fun () ->
        let tagArgs = tags |> List.map (sprintf "%s") |> String.concat " "
        runOrFail run "git" (sprintf "push origin %s" tagArgs) |> ignore)

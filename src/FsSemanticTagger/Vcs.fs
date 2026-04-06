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

let private checkCiForSha (run: string -> string -> CommandResult) (sha: string) : bool =
    let args =
        sprintf "run list --commit %s --json conclusion --jq \".[].conclusion\"" sha

    match run "gh" args with
    | Success output ->
        let conclusions = splitLines output
        conclusions.Length > 0 && conclusions |> Array.forall (fun c -> c = "success")
    | Failure _ -> false

let isCiPassing (run: string -> string -> CommandResult) : bool =
    match getCurrentCommitSha run with
    | None -> false
    | Some sha ->
        if checkCiForSha run sha then
            true
        elif not (hasUncommittedChanges run) then
            // In jj, @ is always a new commit. Check parent if working copy is clean.
            match runSilent run "jj" "log -r @- --no-graph -T commit_id" with
            | Some parentSha when parentSha.Trim() <> "" -> checkCiForSha run (parentSha.Trim())
            | _ -> false
        else
            false

let pushTags (run: string -> string -> CommandResult) (tags: string list) : unit =
    runOrFail run "jj" "git export" |> ignore

    for tag in tags do
        runOrFail run "git" (sprintf "push origin %s" tag) |> ignore

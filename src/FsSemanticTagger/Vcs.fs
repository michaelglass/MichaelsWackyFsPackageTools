module FsSemanticTagger.Vcs

open FsSemanticTagger.Shell
open FsSemanticTagger.Version

let private runOrFail (run: string -> string -> CommandResult) (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure error -> failwithf "%s %s failed: %s" cmd args error

let private runSilent (run: string -> string -> CommandResult) (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

let hasUncommittedChanges (run: string -> string -> CommandResult) : bool =
    match run "jj" "status" with
    | Success output -> not (output.Contains("The working copy is clean"))
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
        output.Split('\n')
        |> Array.map (fun t -> t.Trim())
        |> Array.filter (fun t -> t <> "")
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

let pushTags (run: string -> string -> CommandResult) (tags: string list) : unit =
    runOrFail run "jj" "git export" |> ignore

    for tag in tags do
        runOrFail run "git" (sprintf "push origin %s" tag) |> ignore

module FsSemanticTagger.Vcs

open FsSemanticTagger.Shell
open FsSemanticTagger.Version

let hasUncommittedChanges () : bool =
    match run "jj" "status" with
    | Success output -> not (output.Contains("The working copy is clean"))
    | Failure _ -> true

let tagExists (tag: string) : bool =
    match run "jj" (sprintf "tag list %s" tag) with
    | Success output -> output.Contains(tag)
    | Failure _ ->
        match run "git" (sprintf "tag -l %s" tag) with
        | Success output -> output.Trim() = tag
        | Failure _ -> false

let getLatestTag (prefix: string) : string option =
    // Try jj first, fall back to git
    let output =
        match runSilent "git" (sprintf "tag -l \"%s*\"" prefix) with
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

let commitAndTag (prefix: string) (version: Version) : string =
    let tag = toTag prefix version
    let msg = sprintf "Release %s" (format version)
    runOrFail "jj" (sprintf "commit -m \"%s\"" msg) |> ignore

    // Try jj tag first, fall back to git tag
    match run "jj" (sprintf "tag set %s" tag) with
    | Success _ -> ()
    | Failure _ -> runOrFail "git" (sprintf "tag -a %s -m \"%s\"" tag msg) |> ignore

    tag

let pushTags (tags: string list) : unit =
    runOrFail "jj" "git export" |> ignore

    for tag in tags do
        runOrFail "git" (sprintf "push origin %s" tag) |> ignore

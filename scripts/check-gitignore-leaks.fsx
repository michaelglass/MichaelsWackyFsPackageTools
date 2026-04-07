#!/usr/bin/env dotnet fsi

/// Find files in jj history that match current .gitignore patterns.
/// Uses jj commands for revision traversal and file listing.
///
/// Usage:
///   dotnet fsi scripts/check-gitignore-leaks.fsx        -- check only (exit 1 if leaks found)
///   dotnet fsi scripts/check-gitignore-leaks.fsx --fix  -- untrack leaked files (removes from jj tracking, keeps on disk)

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

let fixMode =
    fsi.CommandLineArgs |> Array.exists (fun a -> a = "--fix")

let run (cmd: string) (args: string list) =
    let psi = ProcessStartInfo(cmd)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    for arg in args do
        psi.ArgumentList.Add(arg)

    use p = Process.Start(psi)
    let stdoutTask = Task.Run(fun () -> p.StandardOutput.ReadToEnd())
    let stderrTask = Task.Run(fun () -> p.StandardError.ReadToEnd())
    p.WaitForExit()
    (p.ExitCode, stdoutTask.Result.TrimEnd(), stderrTask.Result.TrimEnd())

// Parse .gitignore into jj fileset arguments
let parseGitignore (path: string) : string list =
    if not (File.Exists(path)) then
        []
    else
        File.ReadAllLines(path)
        |> Array.toList
        |> List.map (fun s -> s.Trim())
        |> List.filter (fun s -> s.Length > 0 && not (s.StartsWith("#")))
        |> List.collect (fun pattern ->
            let p = pattern.TrimEnd('/')

            if p.Contains("/") then
                [ sprintf "glob:%s" p; sprintf "glob:%s/**" p ]
            else
                [ sprintf "glob:**/%s" p; sprintf "glob:**/%s/**" p ])

let patterns = parseGitignore ".gitignore"

if patterns.IsEmpty then
    printfn "No .gitignore patterns found."
    exit 0

let filesetArgs = patterns

// Check current revision
let _, currentFilesRaw, _ =
    run "jj" ([ "file"; "list"; "-r"; "@"; "--" ] @ filesetArgs)

let currentLeaked =
    currentFilesRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Set.ofArray

// Check all revisions in one pass per revision
let _, revisionsRaw, _ =
    run "jj" [ "log"; "--no-graph"; "-T"; "change_id.short() ++ \"\\n\""; "-r"; "::main" ]

let revisions =
    revisionsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> s.Length > 0)

let mutable allLeaked = currentLeaked

eprintf "Scanning %d revisions" revisions.Length

for rev in revisions do
    let exitCode, files, _ =
        run "jj" ([ "file"; "list"; "-r"; rev; "--" ] @ filesetArgs)

    if exitCode = 0 && files.Length > 0 then
        for f in files.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
            allLeaked <- Set.add f allLeaked

    eprintf "."

eprintfn " done"

let historyOnly = Set.difference allLeaked currentLeaked

if Set.isEmpty allLeaked then
    printfn "No gitignored files found in history."
    exit 0

if not (Set.isEmpty currentLeaked) then
    printfn "Gitignored files currently tracked (%d):" currentLeaked.Count

    for f in currentLeaked do
        printfn "  %s" f

if not (Set.isEmpty historyOnly) then
    let items = historyOnly |> Set.toArray

    printfn "Gitignored files in history only (%d):" items.Length

    for f in items |> Array.truncate 20 do
        printfn "  %s" f

    if items.Length > 20 then
        printfn "  ... and %d more" (items.Length - 20)

printfn
    "\nTotal: %d leaked files (%d current, %d history-only)"
    allLeaked.Count
    currentLeaked.Count
    historyOnly.Count

if fixMode && not (Set.isEmpty currentLeaked) then
    printfn "\nUntracking %d files from jj (files remain on disk)..." currentLeaked.Count

    let code, _, err =
        run "jj" ([ "file"; "untrack"; "--" ] @ (currentLeaked |> Set.toList))

    if code = 0 then
        printfn "Done. Files untracked from jj but still on disk."
        exit 0
    else
        eprintfn "jj file untrack failed: %s" err
        exit 2
elif fixMode then
    printfn "\nNo currently tracked files to untrack. History-only files require jj rebase to clean."
    exit 1
else
    exit 1

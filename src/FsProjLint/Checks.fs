module FsProjLint.Checks

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open System.Xml.Linq

type CheckOutcome =
    | Passed
    | Failed of reason: string

module CheckOutcome =
    let isPassed =
        function
        | Passed -> true
        | Failed _ -> false

    let isFailed =
        function
        | Passed -> false
        | Failed _ -> true

type CheckResult = { Name: string; Outcome: CheckOutcome }

type LintResult =
    { RepoChecks: CheckResult list
      ProjectChecks: (string * CheckResult list) list }

let private fileExists (dir: string) (relativePath: string) : bool =
    File.Exists(Path.Combine(dir, relativePath))

/// Check repo-level requirements.
let checkRepo (dir: string) (hasPackableProjects: bool) : CheckResult list =
    let licenseExists = fileExists dir "LICENSE" || fileExists dir "LICENSE.md"

    let readmeExists = fileExists dir "README.md"
    let editorconfigExists = fileExists dir ".editorconfig"

    let baseChecks =
        [ { Name = "LICENSE exists"
            Outcome =
              if licenseExists then
                  Passed
              else
                  Failed "Missing LICENSE or LICENSE.md" }
          { Name = "README.md exists"
            Outcome = if readmeExists then Passed else Failed "Missing README.md" }
          { Name = ".editorconfig exists"
            Outcome =
              if editorconfigExists then
                  Passed
              else
                  Failed "Missing .editorconfig" } ]

    if hasPackableProjects then
        let docsIndexExists = fileExists dir "docs/index.md"

        baseChecks
        @ [ { Name = "docs/index.md exists"
              Outcome =
                if docsIndexExists then
                    Passed
                else
                    Failed "Missing docs/index.md" } ]
    else
        baseChecks

// --- gitignore-leak check ---------------------------------------------------
//
// Fails when any file matching the repo's .gitignore appears anywhere in git
// history (currently tracked OR history-only). A gitignored file that was ever
// committed leaks into the published history (and any clone/SourceLink), so the
// repo needs an untrack (current) or a history rewrite (history-only).
//
// One efficient pass, NOT a per-revision loop:
//   1. `git log --branches --remotes --diff-filter=A --name-only` -> every path
//      ever added across all real branches/remotes. We deliberately do NOT use
//      `--all`: against a jj-backed store that also walks `refs/jj/keep/*`
//      (every abandoned/conflict/operation-log commit jj retains, full of build
//      artifacts) which is not the published history.
//   2. `git check-ignore --no-index --stdin` as the gitignore oracle (never
//      hand-roll glob matching) -> the subset currently gitignored.
// Leaks = (ever-added) ∩ (currently gitignored).

type private GitContext =
    {
        /// Args inserted before the git subcommand (e.g. --git-dir/--work-tree for
        /// a jj store, or -C <root> for a plain-git checkout).
        PrefixArgs: string list
        /// The directory to launch git in. Pinned to the resolved repo root so
        /// the check is independent of the host process's current directory
        /// (which other code — or parallel tests — may have changed).
        WorkingDir: string
    }

/// Run a git command with the given prefix args + subcommand args, optionally
/// feeding stdin. Returns (exitCode, stdout) or None if git could not be
/// launched at all.
let private runGit (ctx: GitContext) (subArgs: string list) (stdin: string option) : (int * string) option =
    try
        let psi = ProcessStartInfo("git")
        psi.WorkingDirectory <- ctx.WorkingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- stdin.IsSome
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in ctx.PrefixArgs @ subArgs do
            psi.ArgumentList.Add(a)

        match Process.Start(psi) with
        | null -> None
        | p ->
            let stdoutTask = Task.Run(fun () -> p.StandardOutput.ReadToEnd())
            let stderrTask = Task.Run(fun () -> p.StandardError.ReadToEnd())

            match stdin with
            | Some text ->
                p.StandardInput.Write(text)
                p.StandardInput.Close()
            | None -> ()

            let out = stdoutTask.Result
            stderrTask.Result |> ignore
            p.WaitForExit()
            Some(p.ExitCode, out)
    with _ ->
        None

let private splitLines (s: string) : string list =
    s.Split([| '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

/// Resolve how to invoke git for `dir`, distinguishing:
///   * a jj-backed repo  -> Some { --git-dir=<store> --work-tree=<root> }
///   * a plain-git repo   -> Some { -C <root> } (git's own discovery)
///   * not a repo at all  -> None (the check passes; nothing to scan)
let private gitContextFor (dir: string) : GitContext option =
    match Shared.GitDir.resolveGitDir dir with
    | Some store ->
        Some
            { PrefixArgs = [ "--git-dir"; store; "--work-tree"; dir ]
              WorkingDir = dir }
    | None ->
        // resolveGitDir returns None for a native git checkout (it defers to
        // git's discovery) AND for a non-repo. Probe with rev-parse from `dir`
        // itself: a real checkout answers, a bare directory does not.
        let probe = { PrefixArgs = []; WorkingDir = dir }

        match runGit probe [ "rev-parse"; "--is-inside-work-tree" ] None with
        | Some(0, out) when out.Trim() = "true" -> Some probe
        | _ -> None

/// Repo-level check: no gitignored file appears anywhere in git history.
let checkGitignoreLeaks (dir: string) : CheckResult =
    let name = "No gitignored files in git history"

    let outcome =
        match gitContextFor dir with
        | None ->
            // Not a git repo (or git unavailable) — nothing to scan.
            Passed
        | Some ctx ->
            // 1. Every path ever added across real branches/remotes.
            let everAdded =
                match
                    runGit
                        ctx
                        [ "log"
                          "--branches"
                          "--remotes"
                          "--diff-filter=A"
                          "--name-only"
                          "--pretty=format:" ]
                        None
                with
                | Some(_, out) -> splitLines out |> List.distinct
                | None -> []

            if List.isEmpty everAdded then
                Passed
            else
                // 2. Filter to currently-gitignored paths via the git oracle.
                let leaked =
                    match
                        runGit ctx [ "check-ignore"; "--no-index"; "--stdin" ] (Some(String.concat "\n" everAdded))
                    with
                    | Some(_, out) -> splitLines out |> Set.ofList
                    | None -> Set.empty

                if Set.isEmpty leaked then
                    Passed
                else
                    // Split leaks into currently-tracked vs history-only so the
                    // reader knows whether to untrack or rewrite history.
                    let currentlyTracked =
                        match runGit ctx [ "ls-files" ] None with
                        | Some(_, out) -> splitLines out |> Set.ofList
                        | None -> Set.empty

                    let tracked = Set.intersect leaked currentlyTracked
                    let historyOnly = Set.difference leaked tracked
                    let sortedLeaks = leaked |> Set.toList |> List.sort
                    let shown = sortedLeaks |> List.truncate 20

                    let listing = shown |> List.map (fun f -> sprintf "    %s" f) |> String.concat "\n"

                    let more =
                        if sortedLeaks.Length > shown.Length then
                            sprintf "\n    ... and %d more" (sortedLeaks.Length - shown.Length)
                        else
                            ""

                    Failed(
                        sprintf
                            "%d gitignored file(s) in git history (%d currently tracked, %d history-only):\n%s%s"
                            leaked.Count
                            tracked.Count
                            historyOnly.Count
                            listing
                            more
                    )

    { Name = name; Outcome = outcome }

/// Get a property value from an fsproj XDocument.
let getProperty (doc: XDocument) (name: string) : string option =
    doc.Descendants(XName.Get name)
    |> Seq.tryHead
    |> Option.map (fun el -> el.Value)

/// Check if an fsproj has a PackageReference with the given package ID.
let hasPackageRef (doc: XDocument) (packageId: string) : bool =
    doc.Descendants(XName.Get "PackageReference")
    |> Seq.exists (fun el ->
        let includeAttr = el.Attribute(XName.Get "Include")

        match includeAttr with
        | null -> false
        | attr -> attr.Value = packageId)

/// Determine if a project is packable (has PackageId and IsPackable is not "false").
let isPackable (doc: XDocument) : bool =
    let hasPackageId = (getProperty doc "PackageId").IsSome

    let isPackableProp =
        match getProperty doc "IsPackable" with
        | Some "false" -> false
        | _ -> true

    hasPackageId && isPackableProp

let private checkPropertyEquals (doc: XDocument) (propName: string) (expected: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v = expected -> { Name = checkName; Outcome = Passed }
    | Some v ->
        { Name = checkName
          Outcome = Failed(sprintf "%s is '%s', expected '%s'" propName v expected) }
    | None ->
        { Name = checkName
          Outcome = Failed(sprintf "%s not found" propName) }

let private checkPropertyPresent (doc: XDocument) (propName: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v.Trim().Length > 0 -> { Name = checkName; Outcome = Passed }
    | _ ->
        { Name = checkName
          Outcome = Failed(sprintf "%s missing or empty" propName) }

/// Check a single fsproj file.
let checkProject (doc: XDocument) : CheckResult list =
    let allProjectChecks =
        [ checkPropertyEquals doc "TreatWarningsAsErrors" "true" "TreatWarningsAsErrors is true" ]

    if isPackable doc then
        let includesBuildOutput = getProperty doc "IncludeBuildOutput" <> Some "false"

        let packageChecks =
            [ checkPropertyPresent doc "Version" "Version present"
              checkPropertyPresent doc "Description" "Description present"
              checkPropertyPresent doc "Authors" "Authors present"
              checkPropertyPresent doc "PackageLicenseExpression" "PackageLicenseExpression present"
              checkPropertyPresent doc "RepositoryUrl" "RepositoryUrl present"
              checkPropertyPresent doc "RepositoryType" "RepositoryType present"
              checkPropertyEquals doc "GenerateDocumentationFile" "true" "GenerateDocumentationFile is true"
              (let has = hasPackageRef doc "Microsoft.SourceLink.GitHub"

               { Name = "Has Microsoft.SourceLink.GitHub"
                 Outcome =
                   if has then
                       Passed
                   else
                       Failed "Missing Microsoft.SourceLink.GitHub PackageReference" }) ]

        let symbolChecks =
            if includesBuildOutput then
                [ checkPropertyEquals doc "IncludeSymbols" "true" "IncludeSymbols is true"
                  checkPropertyEquals doc "SymbolPackageFormat" "snupkg" "SymbolPackageFormat is snupkg" ]
            else
                []

        allProjectChecks @ packageChecks @ symbolChecks
    else
        allProjectChecks

/// Discover all .fsproj files under the src/ directory.
let discoverProjects (dir: string) : string list =
    let srcDir = Path.Combine(dir, "src")

    if Directory.Exists(srcDir) then
        Directory.GetFiles(srcDir, "*.fsproj", SearchOption.AllDirectories)
        |> Array.toList
        |> List.sort
    else
        []

/// Run all lint checks and return a structured result.
let runLint (dir: string) : LintResult =
    let projects = discoverProjects dir

    let loadResults =
        projects
        |> List.map (fun p ->
            try
                let doc = XDocument.Load(p)
                (p, Ok doc)
            with ex ->
                (p, Error ex.Message))

    let projectChecks =
        loadResults
        |> List.map (fun (p, result) ->
            match result with
            | Ok doc -> (p, checkProject doc)
            | Error msg ->
                (p,
                 [ { Name = "XML parse"
                     Outcome = Failed(sprintf "Failed to parse %s: %s" (Path.GetFileName(p)) msg) } ]))

    let hasPackable =
        loadResults
        |> List.exists (fun (_, result) ->
            match result with
            | Ok doc -> isPackable doc
            | Error _ -> false)

    let repoChecks = checkRepo dir hasPackable @ [ checkGitignoreLeaks dir ]

    { RepoChecks = repoChecks
      ProjectChecks = projectChecks }

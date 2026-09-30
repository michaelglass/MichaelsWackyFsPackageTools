module FsProjLint.Checks

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open System.Xml.Linq

type CheckOutcome =
    | Passed
    | Failed of reason: string
    /// The check could not run here (e.g. no origin remote to compare against).
    /// Neither a pass nor a failure: the exit code ignores it.
    | Skipped of reason: string

module CheckOutcome =
    let isPassed =
        function
        | Passed -> true
        | Failed _
        | Skipped _ -> false

    let isFailed =
        function
        | Failed _ -> true
        | Passed
        | Skipped _ -> false

    let isSkipped =
        function
        | Skipped _ -> true
        | Passed
        | Failed _ -> false

type CheckResult = { Name: string; Outcome: CheckOutcome }

type LintResult =
    {
        RepoChecks: CheckResult list
        ProjectChecks: (string * CheckResult list) list
    }

let private fileExists (dir: string) (relativePath: string) : bool =
    File.Exists(Path.Combine(dir, relativePath))

/// Check repo-level requirements.
let checkRepo (dir: string) (hasPackableProjects: bool) : CheckResult list =
    let licenseExists = fileExists dir "LICENSE" || fileExists dir "LICENSE.md"

    let readmeExists = fileExists dir "README.md"
    let editorconfigExists = fileExists dir ".editorconfig"

    let baseChecks =
        [
            {
                Name = "LICENSE exists"
                Outcome =
                    if licenseExists then
                        Passed
                    else
                        Failed "Missing LICENSE or LICENSE.md"
            }
            {
                Name = "README.md exists"
                Outcome = if readmeExists then Passed else Failed "Missing README.md"
            }
            {
                Name = ".editorconfig exists"
                Outcome =
                    if editorconfigExists then
                        Passed
                    else
                        Failed "Missing .editorconfig"
            }
        ]

    if hasPackableProjects then
        let docsIndexExists = fileExists dir "docs/index.md"

        baseChecks
        @ [
            {
                Name = "docs/index.md exists"
                Outcome =
                    if docsIndexExists then
                        Passed
                    else
                        Failed "Missing docs/index.md"
            }
        ]
    else
        baseChecks

// --- gitignore-leak check ---------------------------------------------------
//
// Fails when any file matching the repo's .gitignore appears in the CURRENT
// BRANCH's history (currently tracked OR history-only). A gitignored file that
// was ever committed on the history you publish from here leaks into the
// published history (and any clone/SourceLink), so the repo needs an untrack
// (current) or a history rewrite (history-only).
//
// Scope is the ancestry of the CURRENT commit ONLY — not every branch/remote.
// A leak that exists only on some other (experiment) branch which is not an
// ancestor of where you are does not fail the gate here; you only care about
// the history you'll publish from this branch.
//
// One pass, NOT a per-revision loop:
//   1. Resolve the current commit. Under jj, `git HEAD` is unreliable (it points
//      at refs/jj/root or a stale detached commit), so ask jj for `@-` — the real
//      checked-out commit, whose sha is a git object in the store. Plain git uses
//      `HEAD`. No commit (unborn/root) means the check passes.
//   2. `git log <commit> --diff-filter=A --name-only` -> every path ever added
//      along that commit's ancestry. NOT `--branches --remotes`/`--all`: those
//      walk experiment branches and (on a jj store) `refs/jj/keep/*`.
//   3. `git check-ignore --no-index --stdin` as the gitignore oracle (never
//      hand-roll glob matching) -> the subset currently gitignored.
// Leaks = (ever-added-on-current-ancestry) ∩ (currently gitignored).

type private GitContext =
    {
        /// Args inserted before the git subcommand (e.g. --git-dir/--work-tree for
        /// a jj store, or -C <root> for a plain-git checkout).
        PrefixArgs: string list
        /// The directory to launch git in. Pinned to the resolved repo root so
        /// the check is independent of the host process's current directory
        /// (which other code — or parallel tests — may have changed). Also the
        /// directory `jj` is invoked in when resolving the current commit.
        WorkingDir: string
        /// True when this is a jj-backed repo (`.jj/` present). The current
        /// commit is then resolved via `jj log @-` (git HEAD is unreliable under
        /// jj); false means a plain-git checkout where `HEAD` is authoritative.
        IsJj: bool
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

/// Run a `jj` command in `workingDir`. Returns (exitCode, stdout) or None if jj
/// could not be launched at all (e.g. not installed). `--ignore-working-copy`
/// keeps this a pure read: jj does not snapshot or mutate the operation log.
let private runJj (workingDir: string) (args: string list) : (int * string) option =
    try
        let psi = ProcessStartInfo("jj")
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add(a)

        match Process.Start(psi) with
        | null -> None
        | p ->
            let stdoutTask = Task.Run(fun () -> p.StandardOutput.ReadToEnd())
            let stderrTask = Task.Run(fun () -> p.StandardError.ReadToEnd())
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
///   * a jj-backed repo  -> Some { --git-dir=<store> --work-tree=<root>; IsJj=true }
///   * a plain-git repo   -> Some { -C <root> (git's own discovery); IsJj=false }
///   * not a repo at all  -> None (the check passes; nothing to scan)
let private gitContextFor (dir: string) : GitContext option =
    match Shared.GitDir.resolveGitDir dir with
    | Some store ->
        Some
            {
                PrefixArgs = [ "--git-dir"; store; "--work-tree"; dir ]
                WorkingDir = dir
                IsJj = true
            }
    | None ->
        // resolveGitDir returns None for a native git checkout (it defers to
        // git's discovery) AND for a non-repo. Probe with rev-parse from `dir`
        // itself: a real checkout answers, a bare directory does not.
        let probe =
            {
                PrefixArgs = []
                WorkingDir = dir
                IsJj = false
            }

        match runGit probe [ "rev-parse"; "--is-inside-work-tree" ] None with
        | Some(0, out) when out.Trim() = "true" -> Some probe
        | _ -> None

/// Resolve the commit whose ancestry defines "the history we publish from
/// here". Returns a git revision string to pass to `git log`, or None when no
/// usable commit exists (unborn branch / jj root) — the caller then passes.
///   * jj-backed: ask jj for the working-copy parent commit_id (`@-`). git HEAD
///     under jj points at refs/jj/root or a stale detached commit, so we never
///     use it. A blank/`0000…` result (root, unborn) yields None.
///   * plain-git: `HEAD`, but only if it resolves (a repo with no commits has
///     an unborn HEAD -> rev-parse fails -> None).
let private resolveCurrentCommit (ctx: GitContext) : string option =
    if ctx.IsJj then
        match runJj ctx.WorkingDir [ "log"; "--no-graph"; "--ignore-working-copy"; "-r"; "@-"; "-T"; "commit_id" ] with
        | Some(0, out) ->
            let sha = out.Trim()

            if sha.Length = 0 || sha |> Seq.forall (fun c -> c = '0') then
                None
            else
                Some sha
        | _ -> None
    else
        match runGit ctx [ "rev-parse"; "--verify"; "--quiet"; "HEAD" ] None with
        | Some(0, out) when out.Trim().Length > 0 -> Some(out.Trim())
        | _ -> None

/// Repo-level check: no gitignored file appears in the current branch's history.
let checkGitignoreLeaks (dir: string) : CheckResult =
    let name = "No gitignored files in git history"

    let outcome =
        match gitContextFor dir with
        | None ->
            // Not a git repo (or git unavailable) — nothing to scan.
            Passed
        | Some ctx ->
            // 1. Every path ever added along the CURRENT commit's ancestry.
            let everAdded =
                match resolveCurrentCommit ctx with
                | None ->
                    // No current commit (unborn branch / jj root) — nothing to scan.
                    []
                | Some commit ->
                    match runGit ctx [ "log"; commit; "--diff-filter=A"; "--name-only"; "--pretty=format:" ] None with
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

// --- RefStamp local-pack guard ----------------------------------------------
//
// A repo that publishes packages must wire in the RefStamp MSBuild guard, so a
// LOCAL `dotnet pack` derives its version from the jj/git source ref and cannot
// produce a release-shaped version. This check is the distribution's enforcement
// arm — sibling repos adopt the guard because fsprojlint tells them to, with the
// exact line to add.
//
// Accepted wirings (any one suffices):
//   * a root Directory.Build.props / Directory.Build.targets with a
//     `<PackageReference Include="RefStamp" .../>` — ONE line, repo-wide;
//   * the same root files with a direct `<Import Project=".../RefStamp.targets"/>`
//     — how the monorepo that OWNS RefStamp dogfoods it;
//   * every packable fsproj carrying the PackageReference itself.
//
// This is a lint of INTENT (the reference/import is present), not an MSBuild
// evaluation — an unparseable root file counts as no guard, never as one.

/// Check if a project XML has an `<Import>` of a RefStamp.targets file.
let private hasRefStampImport (doc: XDocument) : bool =
    doc.Descendants(XName.Get "Import")
    |> Seq.exists (fun el ->
        match el.Attribute(XName.Get "Project") with
        | null -> false
        | attr -> attr.Value.EndsWith("RefStamp.targets"))

/// Repo-level check (packable repos only): the RefStamp local-pack guard is
/// wired in, so `dotnet pack` on a dev machine cannot emit a release-shaped
/// version. `packableProjects` are the parsed packable fsprojs under src/.
let checkRefStampGuard (dir: string) (packableProjects: XDocument list) : CheckResult =
    let hasRefStampGuard (doc: XDocument) =
        hasPackageRef doc "RefStamp" || hasRefStampImport doc

    let rootGuard =
        [ "Directory.Build.props"; "Directory.Build.targets" ]
        |> List.exists (fun name ->
            let path = Path.Combine(dir, name)

            File.Exists(path)
            && (try
                    hasRefStampGuard (XDocument.Load(path))
                with _ ->
                    false))

    let perProjectGuard =
        not (List.isEmpty packableProjects)
        && packableProjects |> List.forall hasRefStampGuard

    {
        Name = "Local packs are ref-stamped (RefStamp)"
        Outcome =
            if rootGuard || perProjectGuard then
                Passed
            else
                Failed(
                    "Missing the RefStamp local-pack guard: add "
                    + "<PackageReference Include=\"RefStamp\" Version=\"<latest>\" PrivateAssets=\"all\" /> "
                    + "to a root Directory.Build.props (one line, applies repo-wide), so a local "
                    + "`dotnet pack` derives its version from the jj/git source ref instead of "
                    + "producing a release-shaped version"
                )
    }

let private checkPropertyEquals (doc: XDocument) (propName: string) (expected: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v = expected -> { Name = checkName; Outcome = Passed }
    | Some v ->
        {
            Name = checkName
            Outcome = Failed(sprintf "%s is '%s', expected '%s'" propName v expected)
        }
    | None ->
        {
            Name = checkName
            Outcome = Failed(sprintf "%s not found" propName)
        }

let private checkPropertyPresent (doc: XDocument) (propName: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v.Trim().Length > 0 -> { Name = checkName; Outcome = Passed }
    | _ ->
        {
            Name = checkName
            Outcome = Failed(sprintf "%s missing or empty" propName)
        }

/// Check a single fsproj file.
let checkProject (doc: XDocument) : CheckResult list =
    let allProjectChecks =
        [
            checkPropertyEquals doc "TreatWarningsAsErrors" "true" "TreatWarningsAsErrors is true"
        ]

    if isPackable doc then
        let includesBuildOutput = getProperty doc "IncludeBuildOutput" <> Some "false"

        let packageChecks =
            [
                checkPropertyPresent doc "Version" "Version present"
                checkPropertyPresent doc "Description" "Description present"
                checkPropertyPresent doc "Authors" "Authors present"
                checkPropertyPresent doc "PackageLicenseExpression" "PackageLicenseExpression present"
                checkPropertyPresent doc "RepositoryUrl" "RepositoryUrl present"
                checkPropertyPresent doc "RepositoryType" "RepositoryType present"
                checkPropertyEquals doc "GenerateDocumentationFile" "true" "GenerateDocumentationFile is true"
                (let has = hasPackageRef doc "Microsoft.SourceLink.GitHub"

                 {
                     Name = "Has Microsoft.SourceLink.GitHub"
                     Outcome =
                         if has then
                             Passed
                         else
                             Failed "Missing Microsoft.SourceLink.GitHub PackageReference"
                 })
            ]

        let symbolChecks =
            if includesBuildOutput then
                [
                    checkPropertyEquals doc "IncludeSymbols" "true" "IncludeSymbols is true"
                    checkPropertyEquals doc "SymbolPackageFormat" "snupkg" "SymbolPackageFormat is snupkg"
                ]
            else
                []

        allProjectChecks @ packageChecks @ symbolChecks
    else
        allProjectChecks

// --- repository URL matches origin -------------------------------------------
//
// nuget.org links a package to its RepositoryUrl. A RepositoryUrl (or a
// github.com PackageProjectUrl) naming another repository than the one the
// project lives in makes that link wrong, often a 404. The repository the
// project lives in is the one its `origin` remote names.

/// A repository on a git host. Only `RepoRef.tryParse` builds one, so the host
/// is lower-case and the name carries no `.git` suffix or trailing slash.
type RepoRef =
    private
        {
            host: string
            owner: string
            name: string
        }

    /// Lower-case host name, e.g. `github.com`.
    member this.Host = this.host
    /// Everything between the host and the name; `group/subgroup` on GitLab.
    member this.Owner = this.owner
    /// The repository name.
    member this.Name = this.name

module RepoRef =
    let private github = "github.com"

    let private scpLike =
        System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9._-]+@(?<host>[^:/\s]+):(?<path>[^/].*)$")

    let private fromHostAndPath (host: string) (path: string) : RepoRef option =
        let segments =
            path.Split('/', System.StringSplitOptions.RemoveEmptyEntries) |> Array.toList

        let host = host.ToLowerInvariant()

        let stripGit (name: string) =
            if name.EndsWith(".git", System.StringComparison.OrdinalIgnoreCase) then
                name.Substring(0, name.Length - 4)
            else
                name

        let ownerAndName =
            match segments with
            // A github.com path past owner/name is a page in the repository.
            | owner :: name :: _ when host = github -> Some(owner, stripGit name)
            | _ :: _ :: _ ->
                let name = List.last segments
                let owner = segments |> List.take (segments.Length - 1) |> String.concat "/"
                Some(owner, stripGit name)
            | _ -> None

        ownerAndName
        |> Option.filter (fun (_, name) -> name.Length > 0)
        |> Option.map (fun (owner, name) ->
            {
                host = host
                owner = owner
                name = name
            })

    /// Parse an https, ssh (`ssh://` or `git@host:owner/repo`), http or git URL
    /// naming a hosted repository. None for anything else, including a local
    /// path, a `file://` URL, or a value still holding an MSBuild `$(...)`.
    let tryParse (url: string) : RepoRef option =
        let url = url.Trim()

        if
            url.Length = 0
            || url.Contains "$("
            || url |> Seq.exists System.Char.IsWhiteSpace
        then
            None
        else
            let scp = scpLike.Match url

            if scp.Success && not (url.Contains "://") then
                fromHostAndPath scp.Groups["host"].Value scp.Groups["path"].Value
            else
                match System.Uri.TryCreate(url, System.UriKind.Absolute) with
                | true, uri when
                    List.contains uri.Scheme [ "https"; "http"; "ssh"; "git"; "git+ssh" ]
                    && uri.Host.Length > 0
                    ->
                    fromHostAndPath uri.Host (System.Uri.UnescapeDataString uri.AbsolutePath)
                | _ -> None

    /// True when both name the same repository. GitHub treats owner and
    /// repository names case-insensitively; other hosts are compared exactly.
    let sameRepository (a: RepoRef) (b: RepoRef) : bool =
        let comparison =
            if a.host = github then
                System.StringComparison.OrdinalIgnoreCase
            else
                System.StringComparison.Ordinal

        a.host = b.host
        && System.String.Equals(a.owner, b.owner, comparison)
        && System.String.Equals(a.name, b.name, comparison)

    /// The repository's https URL, e.g. `https://github.com/owner/name`.
    let toUrl (r: RepoRef) : string =
        sprintf "https://%s/%s/%s" r.host r.owner r.name

/// The repository a checkout's `origin` remote names, or why there is none.
type OriginRemote =
    | Origin of RepoRef
    | NoOrigin of reason: string

/// Read the `origin` remote of the git or jj repository containing `dir`. A jj
/// repository without a colocated `.git` keeps its remotes in its git store.
let resolveOrigin (dir: string) : OriginRemote =
    match gitContextFor dir with
    | None -> NoOrigin "not a git or jj repository, so there is no origin remote to compare against"
    | Some ctx ->
        match runGit ctx [ "remote"; "get-url"; "origin" ] None with
        | Some(0, out) ->
            let url = out.Trim()

            match RepoRef.tryParse url with
            | Some repo -> Origin repo
            | None -> NoOrigin(sprintf "origin remote '%s' is not a hosted repository URL" url)
        | _ -> NoOrigin "no `origin` remote"

/// A property value and the file that sets it.
type private PropertySource = { Value: string; File: string }

/// The value MSBuild would see for `name`: the project's own, else the nearest
/// Directory.Build.props at or above the project's directory, up to `repoDir`.
/// MSBuild imports only that nearest file, so a property it lacks is unset even
/// if a props file further up sets it (unless the nearest one imports it, which
/// this does not follow).
let private effectiveProperty (repoDir: string) (projectPath: string) (doc: XDocument) (name: string) =
    let nonEmpty (file: string) (value: string option) =
        value
        |> Option.map (fun v -> v.Trim())
        |> Option.filter (fun v -> v.Length > 0)
        |> Option.map (fun v -> { Value = v; File = file })

    match nonEmpty projectPath (getProperty doc name) with
    | Some found -> Some found
    | None ->
        let root = Path.GetFullPath(repoDir)

        let rec nearestProps (dir: string) =
            let candidate = Path.Combine(dir, "Directory.Build.props")

            if File.Exists(candidate) then
                Some candidate
            elif Path.GetFullPath(dir) = root then
                None
            else
                match Directory.GetParent(dir) with
                | null -> None
                | parent -> nearestProps parent.FullName

        let projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))

        match nearestProps projectDir with
        | None -> None
        | Some props ->
            try
                nonEmpty props (getProperty (XDocument.Load(props)) name)
            with _ ->
                None

/// Project-level checks (packable projects): RepositoryUrl, and a github.com
/// PackageProjectUrl, name the repository `origin` names. A missing
/// RepositoryUrl adds nothing here ("RepositoryUrl present" fails it); a
/// PackageProjectUrl off github.com (a docs site, `$(RepositoryUrl)`) is not
/// checked.
let checkRepositoryUrls
    (origin: OriginRemote)
    (repoDir: string)
    (projectPath: string)
    (doc: XDocument)
    : CheckResult list =
    let relative (file: string) = Path.GetRelativePath(repoDir, file)

    let compare (property: string) (link: string) (found: PropertySource) (declared: RepoRef option) =
        let outcome =
            match origin, declared with
            | NoOrigin reason, _ -> Skipped reason
            | Origin _, None ->
                Skipped(
                    sprintf
                        "%s '%s' (in %s) is not a hosted repository URL, so it cannot be compared with origin"
                        property
                        found.Value
                        (relative found.File)
                )
            | Origin expected, Some declared when RepoRef.sameRepository expected declared -> Passed
            | Origin expected, Some _ ->
                Failed(
                    sprintf
                        "%s '%s' (in %s) names a different repository than the origin remote, %s, so the package's %s link on nuget.org points at the wrong repository. Fix: set <%s>%s</%s> in %s."
                        property
                        found.Value
                        (relative found.File)
                        (RepoRef.toUrl expected)
                        link
                        property
                        (RepoRef.toUrl expected)
                        property
                        (relative found.File)
                )

        {
            Name = sprintf "%s matches origin remote" property
            Outcome = outcome
        }

    let repositoryUrl =
        effectiveProperty repoDir projectPath doc "RepositoryUrl"
        |> Option.map (fun found -> compare "RepositoryUrl" "Source repository" found (RepoRef.tryParse found.Value))

    let projectUrl =
        effectiveProperty repoDir projectPath doc "PackageProjectUrl"
        |> Option.bind (fun found ->
            match RepoRef.tryParse found.Value with
            | Some declared when declared.Host = "github.com" ->
                Some(compare "PackageProjectUrl" "Project website" found (Some declared))
            | _ -> None)

    List.choose id [ repositoryUrl; projectUrl ]

/// Discover all .fsproj files under the src/ directory, skipping build output,
/// dot-directories and nested checkouts (see `Shared.SourceTree.isSkippedDir`).
let discoverProjects (dir: string) : string list =
    let srcDir = Path.Combine(dir, "src")

    if Directory.Exists(srcDir) then
        Shared.SourceTree.findFiles srcDir "*.fsproj"
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

    // One origin lookup per run, and only when some project is packable.
    let origin = lazy (resolveOrigin dir)

    let projectChecks =
        loadResults
        |> List.map (fun (p, result) ->
            match result with
            | Ok doc when isPackable doc -> (p, checkProject doc @ checkRepositoryUrls origin.Value dir p doc)
            | Ok doc -> (p, checkProject doc)
            | Error msg ->
                (p,
                 [
                     {
                         Name = "XML parse"
                         Outcome = Failed(sprintf "Failed to parse %s: %s" (Path.GetFileName(p)) msg)
                     }
                 ]))

    let packableDocs =
        loadResults
        |> List.choose (fun (_, result) ->
            match result with
            | Ok doc when isPackable doc -> Some doc
            | _ -> None)

    let hasPackable = not (List.isEmpty packableDocs)

    let repoChecks =
        checkRepo dir hasPackable
        @ [ checkGitignoreLeaks dir ]
        @ (if hasPackable then
               [ checkRefStampGuard dir packableDocs ]
           else
               [])

    {
        RepoChecks = repoChecks
        ProjectChecks = projectChecks
    }

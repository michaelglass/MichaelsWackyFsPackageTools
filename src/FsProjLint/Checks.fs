module FsProjLint.Checks

open System.Diagnostics
open System.IO
open System.Threading.Tasks
open System.Xml.Linq
open Shared.MsBuildProject

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
        /// Each Directory.Build.props that does not parse, with its failure. Listed
        /// once per file, however many projects import it.
        PropsChecks: (string * CheckResult) list
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

/// Does `doc` have a `<PackageReference Include="packageId">`?
let private referencesPackage (doc: XDocument) (packageId: string) : bool =
    doc.Descendants(XName.Get "PackageReference")
    |> Seq.exists (fun el ->
        match el.Attribute(XName.Get "Include") with
        | null -> false
        | attr -> attr.Value = packageId)

/// Does the project, or its nearest Directory.Build.props, have a
/// `<PackageReference Include="packageId">`?
let internal hasPackageReference (project: Project) (packageId: string) : bool =
    referencesPackage project.Document packageId
    || project.DirectoryBuildProps
       |> Option.exists (fun (_, doc) -> referencesPackage doc packageId)

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
/// version. `packableProjects` are the repository's packable projects; each is
/// guarded by its own fsproj or its nearest Directory.Build.props.
let internal checkRefStampGuard (dir: string) (packableProjects: Project list) : CheckResult =
    let hasRefStampGuard (doc: XDocument) =
        referencesPackage doc "RefStamp" || hasRefStampImport doc

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
        && packableProjects
           |> List.forall (fun project ->
               hasRefStampGuard project.Document
               || project.DirectoryBuildProps |> Option.exists (snd >> hasRefStampGuard))

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

let private checkPropertyEquals (project: Project) (propName: string) (expected: string) (checkName: string) =
    match propertyValue project propName with
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

let private checkPropertyPresent (project: Project) (propName: string) (checkName: string) =
    match propertyValue project propName with
    | Some _ -> { Name = checkName; Outcome = Passed }
    | None ->
        {
            Name = checkName
            Outcome = Failed(sprintf "%s missing or empty" propName)
        }

/// Check a single project. Properties are read as MSBuild sees them: the
/// project's own, else the nearest Directory.Build.props's.
let internal checkProject (project: Project) : CheckResult list =
    let allProjectChecks =
        [
            checkPropertyEquals project "TreatWarningsAsErrors" "true" "TreatWarningsAsErrors is true"
        ]

    if isPackable project then
        let includesBuildOutput = propertyValue project "IncludeBuildOutput" <> Some "false"

        let packageChecks =
            [
                checkPropertyPresent project "Version" "Version present"
                checkPropertyPresent project "Description" "Description present"
                checkPropertyPresent project "Authors" "Authors present"
                checkPropertyPresent project "PackageLicenseExpression" "PackageLicenseExpression present"
                checkPropertyPresent project "RepositoryUrl" "RepositoryUrl present"
                checkPropertyPresent project "RepositoryType" "RepositoryType present"
                checkPropertyEquals project "GenerateDocumentationFile" "true" "GenerateDocumentationFile is true"
                (let has = hasPackageReference project "Microsoft.SourceLink.GitHub"

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
                    checkPropertyEquals project "IncludeSymbols" "true" "IncludeSymbols is true"
                    checkPropertyEquals project "SymbolPackageFormat" "snupkg" "SymbolPackageFormat is snupkg"
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

/// Project-level checks (packable projects): RepositoryUrl, and a github.com
/// PackageProjectUrl, name the repository `origin` names. A missing
/// RepositoryUrl adds nothing here ("RepositoryUrl present" fails it); a
/// PackageProjectUrl off github.com (a docs site, `$(RepositoryUrl)`) is not
/// checked.
let internal checkRepositoryUrls (origin: OriginRemote) (repoDir: string) (project: Project) : CheckResult list =
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
        property project "RepositoryUrl"
        |> Option.map (fun found -> compare "RepositoryUrl" "Source repository" found (RepoRef.tryParse found.Value))

    let projectUrl =
        property project "PackageProjectUrl"
        |> Option.bind (fun found ->
            match RepoRef.tryParse found.Value with
            | Some declared when declared.Host = "github.com" ->
                Some(compare "PackageProjectUrl" "Project website" found (Some declared))
            | _ -> None)

    List.choose id [ repositoryUrl; projectUrl ]

/// The projects fsprojlint checks, each parsed with its nearest
/// Directory.Build.props: every .fsproj under src/, and every packable one
/// (see `Shared.MsBuildProject.isPackable`) anywhere else in the repository, such
/// as a root-level or tools/ project. A project that does not load is kept, so
/// its failure is reported. The scan skips build output, dot-directories and
/// nested checkouts (see `Shared.SourceTree.isSkippedDir`). Sorted by path.
let internal discoverProjects (dir: string) : (string * Result<Project, LoadError>) list =
    // Every project under src/ is checked, packable or not, as before packable
    // projects elsewhere were found; TreatWarningsAsErrors applies to them all.
    let srcDir =
        Path.Combine(Path.GetFullPath dir, "src") + string Path.DirectorySeparatorChar

    Shared.SourceTree.findFiles dir "*.fsproj"
    |> List.map (fun path -> path, load dir path)
    |> List.filter (fun (path, loaded) ->
        Path.GetFullPath(path).StartsWith(srcDir, System.StringComparison.Ordinal)
        || (match loaded with
            | Ok project -> isPackable project
            | Error _ -> true))

/// Run all lint checks and return a structured result.
let runLint (dir: string) : LintResult =
    let projects = discoverProjects dir

    // One origin lookup per run, and only when some project is packable.
    let origin = lazy (resolveOrigin dir)

    // Each loaded project with whether it is packable, decided once.
    let classified =
        projects
        |> List.map (fun (path, loaded) -> path, loaded |> Result.map (fun project -> project, isPackable project))

    let packableProjects =
        classified
        |> List.choose (fun (_, loaded) ->
            match loaded with
            | Ok(project, true) -> Some project
            | _ -> None)

    let parseFailed (failure: ParseFailure) =
        {
            Name = "XML parse"
            Outcome = Failed(sprintf "Failed to parse %s: %s" (Path.GetFileName failure.File) failure.Message)
        }

    // A project under a broken Directory.Build.props is neither passed nor failed:
    // its properties, and so whether it is packable, cannot be known. The props
    // file fails once, in PropsChecks.
    let propsBroken (props: ParseFailure) =
        {
            Name = "Project checks"
            Outcome =
                Skipped(
                    sprintf
                        "%s does not parse, so this project's properties cannot be read"
                        (Path.GetRelativePath(dir, props.File))
                )
        }

    let projectChecks =
        classified
        |> List.map (fun (path, loaded) ->
            match loaded with
            | Ok(project, true) -> (path, checkProject project @ checkRepositoryUrls origin.Value dir project)
            | Ok(project, false) -> (path, checkProject project)
            | Error(ProjectUnparseable failure) -> (path, [ parseFailed failure ])
            | Error(DirectoryBuildPropsUnparseable props) -> (path, [ propsBroken props ]))

    let propsChecks =
        projects
        |> List.choose (function
            | _, Error(DirectoryBuildPropsUnparseable props) -> Some props
            | _ -> None)
        |> List.distinctBy _.File
        |> List.map (fun props -> props.File, parseFailed props)

    let hasPackable = not (List.isEmpty packableProjects)

    let repoChecks =
        checkRepo dir hasPackable
        @ [ checkGitignoreLeaks dir ]
        @ (if hasPackable then
               [ checkRefStampGuard dir packableProjects ]
           else
               [])

    {
        RepoChecks = repoChecks
        PropsChecks = propsChecks
        ProjectChecks = projectChecks
    }

module FsProjLint.Program

open System.IO
open CommandTree
open FsProjLint.Checks

type Command = | [<Cmd("Lint repo and packable .fsproj files for NuGet OSS readiness"); CmdDefault>] Check

let tree =
    CommandReflection.fromUnion<Command> "Validate repo + .fsproj structure for NuGet-publishable F# projects"

let private rootHelpExtras =
    """
Run from the root of an F# repo. fsprojlint checks every .fsproj under
src/, and every packable .fsproj elsewhere in the repo (the root,
tools/, ...; test, benchmark and example projects are not packable). It
skips build output, dot-directories and nested checkouts, and runs a
fixed set of checks at two levels:

Repo-level checks (run once per repo):
  - LICENSE or LICENSE.md exists at the repo root
  - README.md exists at the repo root
  - .editorconfig exists at the repo root
  - docs/index.md exists (only when the repo has packable projects)
  - No gitignored files in git history: no file matching the repo's
    .gitignore was ever committed on the CURRENT branch's ancestry
    (current-tracked OR history-only). Scope is the branch you're on,
    not every branch/remote — a leak that lives only on an unrelated
    experiment branch does not fail the gate here. A gitignored file in
    this branch's history leaks into the published history and needs an
    untrack (current) or a history rewrite (history-only). Skipped
    (passes) when the directory is not a git/jj repo.
  - Local packs are ref-stamped (RefStamp; only when the repo has
    packable projects): the RefStamp MSBuild guard is wired in — a
    PackageReference to RefStamp in a root Directory.Build.props/targets
    (one line, repo-wide), or in every packable fsproj, or a direct
    Import of RefStamp.targets. The guard makes a local `dotnet pack`
    derive its version from the jj/git source ref, so a dev machine
    cannot produce a release-shaped version (never-re-extracted stale
    NuGet cache entries die with it).

Project properties are read as MSBuild sees them: from the .fsproj,
else from the nearest Directory.Build.props (up to the repo root). A
Directory.Build.props that does not parse fails once, as "XML parse"
naming that file, and the project-level checks of each project under
it are skipped.

Project-level checks (run for every checked .fsproj):
  - TreatWarningsAsErrors is true

Project-level checks (run for each packable .fsproj — those with a
<PackageId>, IsPackable not set to "false", and not an OutputType Exe
without PackAsTool true, i.e. not an example app):
  - Version, Description, Authors, PackageLicenseExpression,
    RepositoryUrl, RepositoryType are present and non-empty
  - GenerateDocumentationFile is true
  - Microsoft.SourceLink.GitHub PackageReference is present (in the
    .fsproj or the nearest Directory.Build.props)
  - IncludeSymbols is true and SymbolPackageFormat is snupkg
    (skipped when IncludeBuildOutput is false)
  - RepositoryUrl, and a github.com PackageProjectUrl, name the
    repository the `origin` remote names (ssh/https spellings, a .git
    suffix and a trailing / are equivalent; github.com names compare
    case-insensitively). Skipped when there is no origin remote or a
    URL is not a hosted repository URL.

Exit code is 1 when any check fails, else 0. Skipped checks print
their reason and do not fail the run.

There are no flags or config files — fsprojlint is intentionally
opinionated about what an OSS-ready F# package looks like.

Examples:
  fsprojlint           # run from repo root; same as 'fsprojlint check'
  fsprojlint check     # explicit subcommand
"""

let private normalizeHelpFlags (argv: string array) : string array =
    argv |> Array.map (fun a -> if a = "-h" || a = "help" then "--help" else a)

[<EntryPoint>]
let main argv =
    let argv = normalizeHelpFlags argv

    match CommandTree.parse tree argv with
    | Ok Check ->
        let cwd = Directory.GetCurrentDirectory()
        let result = runLint cwd

        // Each check with the project it ran on, if any.
        let allChecks =
            (result.RepoChecks |> List.map (fun c -> None, c))
            @ (result.PropsChecks
               |> List.map (fun (props, c) -> Some(Path.GetRelativePath(cwd, props)), c))
            @ (result.ProjectChecks
               |> List.collect (fun (project, checks) ->
                   checks |> List.map (fun c -> Some(Path.GetRelativePath(cwd, project)), c)))

        let label (project: string option, check: CheckResult) =
            match project with
            | Some p -> sprintf "%s (%s)" check.Name p
            | None -> check.Name

        let printSection (heading: string) (tag: string) (checks: (string option * CheckResult) list) =
            if not (List.isEmpty checks) then
                printfn "%s" heading

                for entry in checks do
                    printfn "  %s %s" tag (label entry)

                    match (snd entry).Outcome with
                    | Failed reason
                    | Skipped reason ->
                        for line in reason.Split('\n') do
                            printfn "       %s" line
                    | Passed -> ()

        let failed =
            allChecks |> List.filter (fun (_, c) -> CheckOutcome.isFailed c.Outcome)

        let skipped =
            allChecks |> List.filter (fun (_, c) -> CheckOutcome.isSkipped c.Outcome)

        let passed =
            allChecks |> List.filter (fun (_, c) -> CheckOutcome.isPassed c.Outcome)

        printSection "FAILED:" "FAIL" failed
        printSection "Skipped:" "SKIP" skipped
        printSection "Passed:" "PASS" passed

        let skippedNote =
            if List.isEmpty skipped then
                ""
            else
                sprintf ", %d skipped" skipped.Length

        printfn "\nResult: %d/%d checks passed%s" passed.Length (passed.Length + failed.Length) skippedNote

        if List.isEmpty failed then 0 else 1
    | Error(HelpRequested path) ->
        printfn "%s" (CommandTree.helpForPath tree path "fsprojlint")

        if List.isEmpty path then
            printfn "%s" rootHelpExtras

        0
    | Error VersionRequested ->
        printfn "%s" (CommandTree.renderVersion "fsprojlint")
        0
    | Error e ->
        eprintfn "%s" (CommandTree.renderParseError tree e "fsprojlint")
        if CommandTree.isError e then 1 else 0

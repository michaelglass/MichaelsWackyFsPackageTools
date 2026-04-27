module FsProjLint.Program

open System.IO
open CommandTree
open FsProjLint.Checks

type Command = | [<Cmd("Lint repo and packable .fsproj files for NuGet OSS readiness"); CmdDefault>] Check

let tree =
    CommandReflection.fromUnion<Command> "Validate repo + .fsproj structure for NuGet-publishable F# projects"

let private rootHelpExtras =
    """
Run from the root of an F# repo. fsprojlint scans every .fsproj under
src/ and runs a fixed set of checks at two levels:

Repo-level checks (run once per repo):
  - LICENSE or LICENSE.md exists at the repo root
  - README.md exists at the repo root
  - .editorconfig exists at the repo root
  - docs/index.md exists (only when the repo has packable projects)

Project-level checks (run for every .fsproj under src/):
  - TreatWarningsAsErrors is true

Project-level checks (run for each packable .fsproj — those with a
<PackageId> and IsPackable not set to "false"):
  - Version, Description, Authors, PackageLicenseExpression,
    RepositoryUrl, RepositoryType are present and non-empty
  - GenerateDocumentationFile is true
  - Microsoft.SourceLink.GitHub PackageReference is present
  - IncludeSymbols is true and SymbolPackageFormat is snupkg
    (skipped when IncludeBuildOutput is false)

Exit code is 0 when every check passes, 1 otherwise.

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
        let result = runLint (Directory.GetCurrentDirectory())
        let allChecks = result.RepoChecks @ (result.ProjectChecks |> List.collect snd)

        let failed, passed =
            allChecks |> List.partition (fun c -> CheckOutcome.isFailed c.Outcome)

        if not (List.isEmpty failed) then
            printfn "FAILED:"

            for c in failed do
                printfn "  FAIL %s" c.Name

        if not (List.isEmpty passed) then
            printfn "Passed:"

            for c in passed do
                printfn "  PASS %s" c.Name

        printfn "\nResult: %d/%d checks passed" passed.Length allChecks.Length

        if List.isEmpty failed then 0 else 1
    | Error(HelpRequested path) ->
        printfn "%s" (CommandTree.helpForPath tree path "fsprojlint")

        if List.isEmpty path then
            printfn "%s" rootHelpExtras

        0
    | Error e ->
        eprintfn "%A" e
        1

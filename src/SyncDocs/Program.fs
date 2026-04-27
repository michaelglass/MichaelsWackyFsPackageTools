module SyncDocs.Program

open SyncDocs.Sync

let private warningMessage (warning: DiscoveryWarning) : string =
    match warning with
    | MissingTarget(name, path) -> sprintf "Target docs file missing for %s, create %s" name path
    | MissingSource(name, path) -> sprintf "Source README missing for %s, create %s" name path

let private helpText =
    """Usage: syncdocs <command>

Sync tagged sections from README.md files into docs/ pages, so the
authoritative copy of intro/usage/reference text lives in the README and
the docs site stays in lockstep without copy/paste.

Commands:
  sync     Rewrite docs/ targets to match the current README sources
  check    Compare README sources to docs/ targets; exit 1 if any drift
  --help   Show this help

How discovery works:
  Run from the repo root. syncdocs pairs README files with docs pages
  by convention:

    README.md              ->  docs/index.md
    src/<Project>/README.md ->  docs/<Project>/index.md

  A pair is processed only when both files exist. Missing source or
  target files are reported as warnings, not failures.

How sync markers work:
  In the README (source), wrap a section like this:

    <!-- sync:intro:start -->
    Anything here gets copied into the docs file.
    <!-- sync:intro:end -->

  In the docs page (target), put a matching pair of markers:

    <!-- sync:intro:start -->
    (anything between these gets replaced with the README content)
    <!-- sync:intro:end -->

  Section names ([\w][\w-]*) match between source and target. Sections
  in the source with no matching target markers are silently skipped;
  sections in the target with no matching source markers are left
  untouched.

Exit codes:
  0  success (sync completed, or check found everything in sync)
  1  drift detected (check), failed sync, or argument error

Examples:
  syncdocs check       # CI-friendly drift check
  syncdocs sync        # rewrite docs/ targets in place
"""

let run (argv: string array) (rootDir: string) : Result<int, string> =
    let modeResult =
        match argv with
        | [| "check" |] -> Ok Check
        | [| "sync" |] -> Ok Apply
        | _ -> Error "Usage: syncdocs <sync|check>"

    match modeResult with
    | Error msg -> Error msg
    | Ok mode ->
        let discovery = discoverPairsAndWarnings rootDir

        for w in discovery.Warnings do
            printfn "  Warning: %s" (warningMessage w)

        if discovery.Pairs.IsEmpty then
            printfn "No README.md -> docs/ pairs found"
            Ok 0
        else
            let results =
                discovery.Pairs
                |> List.map (fun pair ->
                    let shortSource = System.IO.Path.GetRelativePath(rootDir, pair.Source)
                    let shortTarget = System.IO.Path.GetRelativePath(rootDir, pair.Target)
                    let result = syncPair mode pair.Source pair.Target

                    match result with
                    | Ok InSync -> printfn "  %s -> %s: in sync" shortSource shortTarget
                    | Ok Updated -> printfn "  %s -> %s: updated" shortSource shortTarget
                    | Ok OutOfSync -> printfn "  %s -> %s: OUT OF SYNC" shortSource shortTarget
                    | Error(SourceMissing _) -> printfn "  %s: source missing (skipped)" shortSource
                    | Error(TargetMissing _) -> printfn "  %s -> %s: target missing (skipped)" shortSource shortTarget

                    result)

            let hasFailure =
                results
                |> List.exists (fun r ->
                    match r with
                    | Ok OutOfSync -> true
                    | Error _ -> true
                    | _ -> false)

            Ok(if hasFailure then 1 else 0)

let private isHelpFlag a = a = "--help" || a = "-h" || a = "help"

[<EntryPoint>]
let main argv =
    if argv <> null && argv |> Array.exists isHelpFlag then
        printf "%s" helpText
        0
    else
        match run argv (System.IO.Directory.GetCurrentDirectory()) with
        | Ok code -> code
        | Error _ ->
            printf "%s" helpText
            1

module SyncDocs.Program

open SyncDocs.Sync

/// Verdict for one configured package (a README that exists by convention).
/// Both the exit code and the "compared N of M pairs" line are folded from the
/// list of these, so the count and the verdict cannot disagree: a pair that was
/// never compared can neither pass nor be silently dropped from the total.
type PairOutcome =
    | Compared of SyncOutcome
    | TargetMissing of package: string * path: string
    | SourceMissing of package: string * path: string

type PairSummary =
    { Compared: int
      Total: int
      Failed: bool }

let summarizePairs (outcomes: PairOutcome list) : PairSummary =
    outcomes
    |> List.fold
        (fun acc outcome ->
            match outcome with
            | Compared InSync
            | Compared Updated ->
                { acc with
                    Compared = acc.Compared + 1
                    Total = acc.Total + 1 }
            | Compared OutOfSync ->
                { acc with
                    Compared = acc.Compared + 1
                    Total = acc.Total + 1
                    Failed = true }
            | TargetMissing _
            | SourceMissing _ ->
                { acc with
                    Total = acc.Total + 1
                    Failed = true })
        { Compared = 0
          Total = 0
          Failed = false }

/// The line printed for an outcome that could not be compared; compared pairs
/// are reported inline as they are processed.
let describePairOutcome (outcome: PairOutcome) : string option =
    match outcome with
    | Compared _ -> None
    | TargetMissing(package, path) -> Some(sprintf "ERROR: docs target missing for %s, looked for %s" package path)
    | SourceMissing(package, path) -> Some(sprintf "ERROR: README source missing for %s, looked for %s" package path)

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

  A README that exists is a configured package and MUST have its docs
  target: a missing target is an error (exit 1) naming the package and
  the path that was looked for, so a check that compared nothing cannot
  read as a clean pass. A docs page with no README is only a warning.
  check ends with "compared N of M pairs".

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
  1  drift detected (check), configured package with no docs target,
     failed sync, or argument error

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

        // A README with no docs target is a configured package that cannot be
        // compared: that is an error below, not a warning. Only an orphaned
        // docs page (target with no README) is still just a warning.
        let missingTargets =
            discovery.Warnings
            |> List.choose (fun w ->
                match w with
                | MissingTarget(name, path) -> Some(TargetMissing(name, path))
                | MissingSource _ -> None)

        let orphanedTargets =
            discovery.Warnings
            |> List.choose (fun w ->
                match w with
                | MissingSource(name, path) -> Some(name, path)
                | MissingTarget _ -> None)

        for name, path in orphanedTargets do
            printfn "  Warning: Source README missing for %s, create %s" name path

        let pairSources = discovery.Pairs |> List.map (fun p -> p.Source) |> List.distinct

        // Standalone docs carry their own `src=` code blocks but have no docs/
        // target — they are refreshed/verified in place. Dedupe against pair
        // sources so a path that is both is processed exactly once (as a pair).
        let standaloneDocs = discoverStandaloneCodeDocs rootDir pairSources

        if discovery.Pairs.IsEmpty && missingTargets.IsEmpty && standaloneDocs.IsEmpty then
            printfn "No README.md -> docs/ pairs found"
            Ok 0
        else
            let runCodeRegions source =
                let shortSource = System.IO.Path.GetRelativePath(rootDir, source)
                let result = syncCodeRegions mode rootDir source

                match result with
                | Ok InSync -> ()
                | Ok Updated -> printfn "  %s: code regions updated" shortSource
                | Ok OutOfSync -> printfn "  %s: code regions OUT OF SYNC" shortSource
                | Error(CodeFileMissing path) -> printfn "  %s: code source missing: %s" shortSource path
                | Error(CodeRegionError(path, regionErr)) ->
                    printfn "  %s: code region error in %s: %A" shortSource path regionErr

                result

            // Stage 1: refresh code-sourced blocks from their .fs/.fsx regions.
            // For pair sources this runs BEFORE README -> docs propagation, so
            // docs pick up the refreshed snippet within a single run. Standalone
            // docs are refreshed in place (no propagation step follows).
            let codeResults = (pairSources @ standaloneDocs) |> List.map runCodeRegions

            // Stage 2: propagate README sources -> docs targets.
            let comparedOutcomes =
                discovery.Pairs
                |> List.map (fun pair ->
                    let shortSource = System.IO.Path.GetRelativePath(rootDir, pair.Source)
                    let shortTarget = System.IO.Path.GetRelativePath(rootDir, pair.Target)

                    match syncPair mode pair.Source pair.Target with
                    | Ok InSync ->
                        printfn "  %s -> %s: in sync" shortSource shortTarget
                        Compared InSync
                    | Ok Updated ->
                        printfn "  %s -> %s: updated" shortSource shortTarget
                        Compared Updated
                    | Ok OutOfSync ->
                        printfn "  %s -> %s: OUT OF SYNC" shortSource shortTarget
                        Compared OutOfSync
                    | Error(SyncError.SourceMissing _) -> SourceMissing(shortSource, shortSource)
                    | Error(SyncError.TargetMissing _) -> TargetMissing(shortSource, shortTarget))

            let pairOutcomes = comparedOutcomes @ missingTargets

            for line in pairOutcomes |> List.choose describePairOutcome do
                printfn "  %s" line

            let codeFailure =
                codeResults
                |> List.exists (fun r ->
                    match r with
                    | Ok OutOfSync -> true
                    | Error _ -> true
                    | _ -> false)

            let summary = summarizePairs pairOutcomes

            let verb =
                match mode with
                | Check -> "compared"
                | Apply -> "synced"

            printfn "  %s %d of %d pairs" verb summary.Compared summary.Total

            Ok(if codeFailure || summary.Failed then 1 else 0)

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

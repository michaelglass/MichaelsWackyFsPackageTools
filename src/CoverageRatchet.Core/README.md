# CoverageRatchet.Core

Core library for Cobertura XML coverage parsing, per-file threshold checking, ratcheting, and merging. This is the embeddable, no-CLI version of [CoverageRatchet](https://www.nuget.org/packages/CoverageRatchet).

Use this package to integrate coverage ratcheting directly into your own tools, scripts, or build systems without taking a dependency on the CLI entry point.

## Installation

```bash
dotnet add package CoverageRatchet.Core
```

## Modules

### `CoverageRatchet.Cobertura`

Parses Cobertura XML reports into structured coverage data.

```fsharp
open CoverageRatchet.Cobertura

// Parse a single XML string
let files: FileCoverage list = parseXml xmlContent

// Parse a file on disk
let files: FileCoverage list = parseFile "/path/to/coverage.cobertura.xml"

// Merge multiple XML files (union of line/branch hits)
let files: FileCoverage list = parseFiles [ "/path/to/a.xml"; "/path/to/b.xml" ]

// Find coverage files in a directory tree
let paths: string list = findCoverageFiles "/path/to/search"
let latest: string option = findCoverageFile "/path/to/search"
```

`FileCoverage` carries per-file line and branch coverage percentages:

```fsharp
type FileCoverage =
    { FileName: string
      LinePct: float
      BranchPct: float
      BranchesCovered: int
      BranchesTotal: int }
```

For branch gap analysis, use the lower-level API:

```fsharp
let rawLines = extractRawLines xmlContent
let gaps: FileBranchGaps list = buildBranchGaps rawLines
```

Files from paths like `paket-files/`, `vendor/`, `node_modules/`, and `.fable/` are automatically excluded, as are files matching `Test`, `AssemblyInfo`, or `AssemblyAttributes` in their name. Only `.fs` files are included.

### `CoverageRatchet.Thresholds`

Loads, saves, and checks per-file coverage thresholds from a `coverage-ratchet.json` config.

```fsharp
open CoverageRatchet.Thresholds

// Load config (returns 100%/100% defaults if file doesn't exist)
let config: Config = loadConfig "coverage-ratchet.json"

// Check coverage against thresholds
match check config files with
| AllPassed -> printfn "All files passed"
| SomeFailed failed ->
    for r in failed do
        printfn $"{r.File.FileName}: line {r.File.LinePct}%% < {r.LineThreshold}%%"

// Build per-file results with threshold annotations
let results: FileResult list = buildFileResults config files
```

`Config` holds default thresholds (100%/100%) plus per-file `Override` entries:

```fsharp
type Config =
    { DefaultLine: float
      DefaultBranch: float
      Overrides: Map<string, Override> }

type Override =
    { Line: float
      Branch: float
      Reason: string option
      Platform: Platform option }
```

For multi-platform configs (where the same file may have different thresholds per OS), use `RawConfig`/`loadRawConfig`/`saveRawConfig` to preserve all platform entries. `resolveConfig` collapses a `RawConfig` to a `Config` for the current platform.

### `CoverageRatchet.Ratchet`

Tightens or loosens thresholds based on current coverage.

```fsharp
open CoverageRatchet.Ratchet

// Ratchet: only raise thresholds, never lower them
let newConfig: Config = ratchet config files

// With status reporting
match ratchetWithStatus config files with
| NoChanges -> ()
| Tightened newRaw -> saveRawConfig "coverage-ratchet.json" newRaw
| Failed(newRaw, failedFiles) ->
    for f in failedFiles do printfn $"FAIL: {f}"

// Operate on RawConfig directly (preserves multi-platform entries)
let newRaw: RawConfig = ratchetRaw raw files
let status: RatchetStatus = ratchetRawWithStatus raw files

// Loosen: set thresholds to current actual coverage
let newConfig: Config = loosen config files
let newRaw: RawConfig = loosenRaw raw files

// Merge thresholds from a CI platform (for cross-platform workflows)
let newRaw: RawConfig = mergeFromCi raw Platform.Linux ciResults

// Parse coverage-thresholds artifact JSON (shape produced by CoverageRatchet check-json)
let platform, results = parseCiThresholds jsonString
```

`ratchet` floors fractional coverage percentages (e.g. 80.7% → threshold 80.0) so thresholds are stable integers. It never introduces new overrides — only tightens or removes existing ones as coverage improves.

`loosen` sets every file's threshold to its current actual coverage, adding new overrides for files below 100% with `reason = "loosened automatically"`.

### `CoverageRatchet.Merge`

Merges two Cobertura XML reports at the XML level, taking the max hit count per line. Useful for layering a partial (impact-filtered) test run onto a persisted full-run baseline.

```fsharp
open CoverageRatchet.Merge

// Merge partialPath onto baselinePath, write result to outputPath
mergeFiles baselinePath partialPath outputPath

// For each coverage.cobertura.xml in searchDir, merge onto sibling coverage.baseline.xml
mergeIntoBaselines searchDir

// Advance baselines to current coverage after a full test run
refreshBaselines searchDir
```

## Configuration Format

The JSON config (`coverage-ratchet.json`) used by `Thresholds` and `Ratchet`:

```json
{
  "overrides": {
    "Program.fs": {
      "line": 85,
      "branch": 77,
      "reason": "CLI entry point — exit calls not coverable"
    },
    "Shell.fs": [
      { "line": 60, "branch": 50, "reason": "process execution", "platform": "macos" },
      { "line": 45, "branch": 40, "reason": "process execution", "platform": "linux" }
    ]
  }
}
```

Files not listed in `overrides` default to 100% line and 100% branch coverage. Per-file overrides can be a single object (platform-agnostic) or an array of platform-specific objects (`"macos"`, `"linux"`, `"windows"`).

## License

MIT

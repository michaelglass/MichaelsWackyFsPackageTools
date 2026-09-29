# CoverageRatchet.Core

Core library for Cobertura XML coverage parsing, per-file threshold checking, ratcheting, and merging. This is the embeddable, no-CLI version of [CoverageRatchet](https://www.nuget.org/packages/CoverageRatchet).

Use this package to integrate coverage ratcheting directly into your own tools, scripts, or build systems without taking a dependency on the CLI entry point.

> **Status: early alpha, and substantially AI-written.** Runs the author's own F# OSS repos daily, but the API shifts between versions and rough edges are expected — your mileage may vary. Issues and PRs welcome.

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

`FileCoverage` carries per-file coverage as both a ratio and its two components:

<!-- sync:file-coverage:start src=src/CoverageRatchet.Core/Cobertura.fs -->
```fsharp
/// Per-file coverage data parsed from a Cobertura XML report.
///
/// `*Total` counts drift between runs (a line is only emitted once its method is
/// JIT-compiled); `*Covered` counts are stable for unchanged code, which is why
/// count floors gate on them.
type FileCoverage =
    {
        FileName: string
        LinePct: float
        BranchPct: float
        LinesCovered: int
        LinesTotal: int
        BranchesCovered: int
        BranchesTotal: int
    }
```
<!-- sync:file-coverage:end -->

`LinesCovered`/`BranchesCovered` are the numerator, `LinesTotal`/`BranchesTotal` the denominator. They are kept separately because only the numerator is stable — see [Count floors](#count-floors).

For branch gap analysis, use the lower-level API:

```fsharp
let rawLines = extractRawLines xmlContent
let gaps: FileBranchGaps list = buildBranchGaps rawLines
```

<!-- sync:reader-rules:start -->
By default the reader reads every `.fs`, `.cs` and `.vb` file, and skips a file when a directory in its path is:

- a test project: named `tests` or `test`, or ending in `.Tests` or `.Test` (e.g. `tests/MyLib.Tests/`)
- build output: `obj`, where the SDK writes `AssemblyInfo`/`AssemblyAttributes` and source-generator output
- vendored code: `paket-files`, `vendor`, `node_modules` or `.fable`

Every comparison ignores case, and a file's own name never matters: a production `TestKit.fs` is read. Scripts (`.fsx`, `.csx`) are not read because they are not compiled into an assembly under test; signature files (`.fsi`) hold no executable lines.

The rules see the path exactly as the report records it. `dotnet test --coverage` records absolute paths, so the directories above your checkout count too: a checkout under a directory named `tests` reads nothing, which the CLI reports as nothing measured (exit 2) and `targets` explains.

Test-support code compiled into a library that is not itself a test project (a shared `Tests.Common`, say) reaches the report from wherever it lives. Keep it out of every consumer of the report at once with an assembly-level attribute in that project:

```xml
<ItemGroup>
  <AssemblyAttribute Include="System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute" />
</ItemGroup>
```

An attribute on a module is not enough in F#: the module's top-level values are initialised by a compiler-generated `<StartupCode$…>` class, which carries no attribute.
<!-- sync:reader-rules:end -->

<!-- sync:reader-defaults:start src=src/CoverageRatchet.Core/Cobertura.fs -->
```fsharp
/// Every source language, minus test projects, build output and vendored code.
let defaults =
    {
        IncludedExtensions = Array.copy sourceExtensions
        ExcludedDirectories =
            [|
                // test projects: tests/, test/, MyLib.Tests/, MyLib.Test/
                Named "tests"
                Named "test"
                NameEndsWith ".Tests"
                NameEndsWith ".Test"
                // SDK-generated sources (AssemblyInfo, AssemblyAttributes, source generators)
                Named "obj"
                // vendored code
                Named "paket-files"
                Named "vendor"
                Named "node_modules"
                Named ".fable"
            |]
    }
```
<!-- sync:reader-defaults:end -->

#### Reader options and skipped files

`readReports` reads one or more reports in a single pass with the options you pass, and returns the lines it read alongside the files it skipped:

<!-- sync:reader-options:start src=src/CoverageRatchet.Core/Cobertura.fs -->
```fsharp
/// A rule on one directory in a source file's path, compared ignoring case.
type DirectoryRule =
    /// The directory is named exactly this, e.g. `obj`.
    | Named of name: string
    /// The directory's name ends with this, e.g. `.Tests`.
    | NameEndsWith of suffix: string

/// Which `<class>` elements of a Cobertura report the reader reads.
///
/// A file is read when its name ends with one of `IncludedExtensions` and no directory
/// in its path matches one of `ExcludedDirectories`, both ignoring case. The rules see
/// the path exactly as the report records it, which for an absolute path includes the
/// directories above the checkout.
type ReaderOptions =
    {
        IncludedExtensions: string[]
        ExcludedDirectories: DirectoryRule[]
    }
```
<!-- sync:reader-options:end -->

<!-- sync:exclusion-reason:start src=src/CoverageRatchet.Core/Cobertura.fs -->
```fsharp
/// Which `ReaderOptions` filter skipped a file, and the rule that matched.
type ExclusionReason =
    | ExcludedByExtension of extension: string
    | ExcludedByDirectory of rule: DirectoryRule

/// A file in the report that the reader skipped, keyed by base name like `FileCoverage`.
type ExcludedFile =
    {
        FileName: string
        Reason: ExclusionReason
    }
```
<!-- sync:exclusion-reason:end -->

`ReaderOptions.includingOnly` narrows the defaults to some of the source languages, and rejects an empty list, an extension without its leading `.`, and an extension the reader does not measure:

```fsharp
let fsOnly =
    match ReaderOptions.includingOnly [ ".fs" ] with
    | Ok options -> options
    | Error message -> failwith message

let report: Report = readReports fsOnly [ File.ReadAllText "coverage.cobertura.xml" ]

let files: FileCoverage list = buildCoverage report.Lines

for e in report.Excluded do
    printfn "%s — %s" e.FileName (ExclusionReason.describe e.Reason)
    // Handler.cs — extension ".cs" is not read
    // HandlerTests.fs — in a directory named "tests"
```

To read a language outside the defaults, or to change the directory rules, build the record yourself: `{ ReaderOptions.defaults with IncludedExtensions = [| ".razor" |] }`.

The parameterless functions (`parseXml`, `parseFiles`, `extractRawLines`, …) use `ReaderOptions.defaults`. The CLI reads its options from the floor file with `Thresholds.loadReaderOptions path`: `ReaderOptions.defaults`, narrowed by the file's `"includedExtensions"` list when it has one, or an `Error` naming the file and what is wrong with the list.

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

`Config` holds default thresholds (100%/100%) plus two independent kinds of per-file floor — percentage `Override` entries and absolute `CountFloor` entries:

<!-- sync:threshold-types:start src=src/CoverageRatchet.Core/Thresholds.fs -->
```fsharp
/// A per-file PERCENTAGE floor. `Line` and `Branch` are percentages (0-100).
type Override =
    {
        Line: float
        Branch: float
        Reason: string option
        Platform: Platform option
    }

/// A per-file floor on the absolute COUNT of covered lines / covered branches.
///
/// Deliberately a separate type living in a separate config section
/// ("countFloors") from the percentage `Override` ("overrides"). The two are
/// never positionally interchangeable: `{"line": 93}` is 93 PERCENT, while
/// `{"coveredLines": 93}` is 93 LINES. A config written before count floors
/// existed has no "countFloors" section at all, so it can never be reread as
/// a set of counts.
///
/// Why counts: the coverage collector emits a source line only when its method
/// JIT-compiles, so the percentage denominator wobbles between runs while the
/// numerator does not (ADR 0019). Counts gate on the stable quantity.
type CountFloor =
    {
        CoveredLines: int
        CoveredBranches: int
        Reason: string option
        Platform: Platform option
    }

type Config =
    {
        DefaultLine: float
        DefaultBranch: float
        Overrides: Map<string, Override>
        CountFloors: Map<string, CountFloor>
    }
```
<!-- sync:threshold-types:end -->

Count floors are checked by `checkCounts`, which is independent of `check`:

```fsharp
// Percentage floors
match check config files with
| AllPassed -> ()
| SomeFailed failed -> for r in failed do printfn $"{r.File.FileName} below threshold"

// Absolute covered-count floors — only files that HAVE a floor are checked
match checkCounts config files with
| CountsAllPassed -> ()
| CountsFailed failed ->
    for r in failed do
        printfn $"{r.File.FileName}: {r.File.LinesCovered} covered lines < floor {r.Floor.CoveredLines}"
```

For multi-platform configs (where the same file may have different thresholds per OS), use `RawConfig`/`loadRawConfig`/`saveRawConfig` to preserve all platform entries. `resolveConfig` collapses a `RawConfig` to a `Config` for the current platform, and `resolveConfigFor` for a named one (what a CI runner on that platform enforces); `overrideEntriesToJson` renders one file's entries exactly as `saveRawConfig` would write them; `toRawConfig` widens one back (it cannot invent platform entries that resolving discarded).

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

// Count floors: raise monotonically, or re-baseline to current counts
let newConfig: Config = ratchetCountFloors config files
let newConfig: Config = baselineCountFloors config files
let newRaw: RawConfig = ratchetCountFloorsRaw raw files
let newRaw: RawConfig = baselineCountFloorsRaw raw files
```

`ratchet` floors fractional coverage percentages (e.g. 80.7% → threshold 80.0) so thresholds are stable integers. It never introduces new overrides — only tightens or removes existing ones as coverage improves.

`loosen` sets every file's threshold to its current actual coverage, adding new overrides for files below 100% with `reason = "loosened automatically"`.

`ratchetCountFloors` is monotonic and **never enrols new files** — an impact-filtered partial run must not be able to write a floor from coverage that never ran. `baselineCountFloors` records current counts for every observed file and *may lower* a floor; it is both the bootstrap and the deliberate re-baseline after removing covered code, and it preserves any recorded `reason`.

`loosen`, `loosenRaw` and `mergeFromCi` operate on **percentage** floors only. There is deliberately no automatic lowering path for count floors: lowering one is the human decision that `baselineCountFloors` exists to record.

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

## Count floors

Coverage percentage has a denominator problem. The .NET collector emits a source line only when its containing method is **JIT-compiled** during the run, so the set of emitted lines — the percentage denominator — shifts with execution path, machine load, and how many projects you pool together. The numerator does not.

The same file, same tests, same hits, can therefore report very different percentages:

```
Foo.fs from one project's report:      383 / 412 = 93.0%
Foo.fs pooled with a project that
covers none of it:                     383 / 639 = 59.9%
```

Count floors gate on **383**, the number that held still. `checkCounts` enforces them, `ratchetCountFloors` raises them, `baselineCountFloors` records or re-baselines them.

**The trade-off, stated plainly:** a count floor cannot tell a deleted test from deleted code — both lower the count, and the only signal that would distinguish them is the emitted-line total, precisely the number that is not trustworthy. So the library does not guess. A legitimate decrease is resolved by calling `baselineCountFloors`, which is the same function that bootstraps.

Baselined floors are written **platform-less**, so one baseline guards every platform. Nothing in this library synthesises a platform-tagged count floor: a floor tagged `macos` is invisible to a Linux-only CI, so tagging is left to whoever knows which platform measured the numbers.

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
  },
  "countFloors": {
    "Program.fs": { "coveredLines": 383, "coveredBranches": 41 }
  }
}
```

There are **two independent sections, and their numbers are not the same kind of number**:

| Section | Key | Unit | Missing file means |
|---------|-----|------|--------------------|
| `overrides` | `line` / `branch` | **percent** (0–100) | must reach 100% / 100% |
| `countFloors` | `coveredLines` / `coveredBranches` | **absolute count** of covered lines / branches | **no count floor at all** — counts are opt-in per file |

> **`"line": 93` means 93 percent. `"coveredLines": 93` means 93 lines.**
> The keys differ precisely so the two can never be positionally confused, and they live in separate sections so a config written before count floors existed can never be reread as a set of counts. A config with no `countFloors` section enforces percentages exactly as it always did.

`reason` and `platform` are accepted in both sections. Per-file entries can be a single object (platform-agnostic) or an array of platform-specific objects (`"macos"`, `"linux"`, `"windows"`).

## License

MIT

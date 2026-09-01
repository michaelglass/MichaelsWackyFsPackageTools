# CoverageRatchet

Per-file code coverage enforcement that only goes up. CoverageRatchet reads your Cobertura XML coverage reports, compares each file against its threshold, and helps prevent coverage from regressing.

> **Status: early alpha, and substantially AI-written.** Runs the author's own F# OSS repos daily, but behavior and flags shift between versions and rough edges are expected — your mileage may vary. Issues and PRs welcome.

## How It Works

1. Your test suite generates a Cobertura XML coverage report (most .NET coverage tools support this format).
2. CoverageRatchet reads the report and compares each file's line and branch coverage against its threshold.
3. **`check`** fails the build if any file drops below its threshold.
4. **`ratchet`** (the default command) updates thresholds to match current coverage -- thresholds only go up, not down.
5. **`loosen`** sets thresholds to whatever coverage is right now, so `check` passes immediately.
6. **`baseline-lines`** records each file's current *covered-line count* as a floor (see [Count floors](#count-floors)).
7. **`targets`** lists files sorted by coverage to find improvement opportunities.
8. **`gaps`** shows uncovered branch points per file with line numbers.

The default threshold for every file is **100% line and branch coverage**. Files that can't easily reach 100% (like CLI entry points) can get per-file overrides with a documented reason.

There are two independent kinds of floor, and they never mix: **percentage** floors in `overrides`, and **absolute covered-line count** floors in `countFloors`. Percentages are what most projects want. Count floors exist for the case where the percentage *denominator* is not trustworthy — see below.

## Installation

```bash
dotnet tool install -g CoverageRatchet
```

## Usage

### Ratchet (default)

Just run `coverageratchet` with no arguments to ratchet thresholds upward:

```bash
coverageratchet
```

This recursively searches for a `coverage.cobertura.xml` file, compares each file against its threshold, and tightens thresholds where coverage has improved. Exit codes:

| Exit code | Meaning |
|-----------|---------|
| 0 | All thresholds met, no config changes needed |
| 1 | Config was updated (some thresholds tightened) |
| 2 | Some files are below their threshold |

You can also run it explicitly as `coverageratchet ratchet`.

### Check coverage (CI)

```bash
coverageratchet check
```

| Exit code | Meaning |
|-----------|---------|
| 0 | Every configured floor was measured, and every F# file in the report met its threshold |
| 1 | At least one **measured** file fell below its threshold |
| 2 | This run cannot answer the question — nothing was measured, or a configured floor has no row in the report |

#### The expected set comes from the config, not from the report

`check` used to derive its entire file list from the coverage XML. A file with a
recorded floor that had no row in the report was not failed, not warned about,
and not counted — it silently disappeared, and the run printed `7/7 files
passed` and exited 0. That sentence reads the same whether the project has 7
files or 50.

So the set a run is *obliged* to measure now comes from `overrides` and
`countFloors` in the config. If the report cannot speak to one of them, `check`
names it and exits 2. Two things put a file on that list and the tool cannot
tell them apart — floors are keyed by file name, so there is no path to go
looking for:

- **the report is partial** — a filtered or crashed test run, or a report read
  while it was still being written. Re-run the full suite.
- **the file is gone** — delete its entry from the config.

This completeness check is deliberately limited to those explicit, active
configured floors. The 100%/100% default still applies to every file that does
appear in the report, but a file with no `overrides` or `countFloors` entry
leaves no name in the config to detect if it is absent. Projects that require
full-set detection must therefore keep an explicit floor for every expected
file (a count floor is sufficient).

Exit 2 also covers a report with no F# file in it at all: the wrong
`--search-dir`, a collector that wrote nothing, a report read mid-write. Zero
files examined is not zero files failing.

#### A file the reader never read leaves no name either

The completeness check above closes the gap between the config and the report.
There is one more gap below it: a file the *reader* filtered out never reaches
the report-side list at all. It has no coverage row, so it never gets a floor,
so it is not in `overrides` for the check to miss — it is absent from both the
numerator and the denominator, and `3/3` reads exactly like `4/4`.

The filters are conventional and usually right — generated files, vendored
paths, and names containing `Test`. But `Test` is matched anywhere in the name,
so a production file called `TestKit.fs`, `TestHarness.fs` or `TestServer.fs` is
dropped too, and those are precisely the files whose coverage someone would want
ratcheted.

`check` now says how many were dropped:

```
Result: 3/3 files in the report passed (1 more was excluded by the reader; see `targets`)
```

and `targets` names them and says which filter decided:

```
  3 files

  Not read by the coverage reader:
    TestKit.fs — name contains "Test"
```

The rule itself is unchanged — this reports what the reader did rather than
altering it, so an existing project measures exactly the files it measured
before.

### Loosen thresholds

If you need `check` to pass right now (e.g., after a big refactor that dropped coverage), loosen sets every file's threshold to its current actual coverage:

```bash
coverageratchet loosen
```

This always exits 0. Files that were already at 100% don't get an override. New overrides get the reason `"loosened automatically"`.

### Show improvement targets

```bash
coverageratchet targets
```

Lists all files sorted by line coverage (lowest first), so you can see where to focus testing effort. Always exits 0.

### Show branch coverage gaps

```bash
coverageratchet gaps
```

Shows uncovered branch points per file, with specific line numbers and how many branches are covered vs total. Files are sorted by gap count (most gaps first). Always exits 0.

### Export coverage as JSON (for CI)

```bash
coverageratchet check-json [config-path] [output-path]
```

Writes machine-readable coverage results. The exit code really does match `check` now — same verdict, so count floors and unmeasured floors are included, where previously `check-json` looked only at percentage floors on files the report happened to contain. The results file is written before the verdict is rendered, so CI still has something to upload on a red run. Used by CI workflows to upload coverage data as an artifact.

### Sync thresholds from CI

```bash
coverageratchet loosen-from-ci [config-path]
```

Pushes current code, polls CI, and if coverage fails:
1. Downloads the `coverage-thresholds` artifact
2. Merges CI platform thresholds into local config (splitting non-platform entries if needed)
3. Commits, pushes, and re-polls CI

Requires the `gh` CLI plus Git or Jujutsu (jj).

#### Artifact contract

`loosen-from-ci` expects CI to upload an artifact named `coverage-thresholds`
containing one file per project: `coverage-thresholds-<project>.json`. Each
file is the output of `check-json` with shape:

```json
{
  "platform": "linux",
  "results": {
    "Foo.fs": { "line": 72, "branch": 54 },
    "Bar.fs": { "line": 80, "branch": 100 }
  }
}
```

`platform` is one of `linux`, `macos`, `windows`. `<project>` matches the
suffix of the local `coverage-ratchet-<project>.json` config; files named
`coverage-thresholds-default.json` (or `coverage-thresholds-.json`) merge
into the default `coverage-ratchet.json` config. The reusable build workflow
`michaels-wacky-build.yml` produces this artifact automatically.

### Partial-run survival with baselines

If your test runner only runs a *subset* of tests (e.g. a test-impact analyzer like fs-hot-watch's TestPrune runs only tests affected by your changes), the coverage XML from that partial run will reflect only the lines touched by that subset. Lines covered by tests that *didn't* re-run show zero hits. Coverage appears to drop; `check` fails even though nothing regressed.

CoverageRatchet can guard against this by merging each run onto a per-project **baseline** — a snapshot of the last full run. Merging takes the max hits per line across baseline and current, so partial runs can only raise coverage, never lower it.

**Layout** — per test project:

```
coverage/<project>/
  coverage.baseline.xml   # last full run; source of truth
  coverage.cobertura.xml  # what check reads; merged after every run
```

**Flow:**

```bash
# Before each check, layer baseline onto current run. Bootstraps baseline
# on the first run automatically if it doesn't exist yet.
coverageratchet --search-dir coverage check --merge-baselines

# After a deliberate *full* test run (no impact filter), advance baseline:
coverageratchet --search-dir coverage refresh-baseline
```

If `FSHW_RAN_FULL_SUITE=true` is set when `check --merge-baselines` runs AND the check passes, the baseline is refreshed automatically — useful when a test runner can tell you whether it just ran the full suite.

**One-shot merge** — for ad-hoc merges outside the standard layout:

```bash
coverageratchet merge <baseline.xml> <partial.xml> <output.xml>
```

**Gotchas:**

- **Deleted tests leave stale hits in the baseline until it's refreshed.** If you delete a test that was the only one covering some lines, those lines keep their old hit counts until `refresh-baseline` runs. Budget a periodic full run (daily CI, for example) to catch this.
- **New source files added in a partial-only run** are measured only by whichever tests ran — that's all the merger knows about. Ratchet thresholds for new files will reflect partial coverage until the next full run refreshes the baseline.
- Baselines are a safety net against false drops, not a substitute for periodic full runs.

### Excluding upstream package source files

If your project takes a **local** NuGet `PackageReference` (e.g. a sibling library you build locally instead of consuming from a feed), `dotnet-coverage` will happily instrument the upstream package's source files and emit them in your Cobertura XML. They aren't your code — you can't fix their coverage — but CoverageRatchet will still hold you to the default 100% / 100% on every file it sees.

**Recommended fix: exclude at the instrumentation layer.** Add a `.coverage-settings.xml` next to your `dotnet test` invocation and pass it via `--settings`:

```xml
<!-- .coverage-settings.xml -->
<CodeCoverage>
  <ModulePaths>
    <Exclude>
      <ModulePath>.*UnionConfig.*</ModulePath>
      <ModulePath>.*FsHotWatch.*</ModulePath>
    </Exclude>
  </ModulePaths>
</CodeCoverage>
```

```bash
dotnet test --settings .coverage-settings.xml --coverage --coverage-output-format cobertura ...
```

The upstream files never get instrumented, never appear in the Cobertura XML, and never reach CoverageRatchet. This is the right layer for the fix:

- One source of truth — IDE coverage gutters, Codecov, and any other consumer of the same XML also see them excluded.
- Works for any threshold tool, not just CoverageRatchet.
- Matches what dotnet-coverage natively understands (assembly / module patterns).

CoverageRatchet has **no config-level exclude list** by design — exclusions belong at the instrumentation boundary, not in the threshold checker. The built-in path filters (`paket-files/`, `vendor/`, `node_modules/`, `.fable/`, plus `Test*` / `AssemblyInfo*` / `AssemblyAttributes*` filenames) only exist because they are universal F# OSS conventions, not project-specific exclusions.

### Custom search directory

By default, CoverageRatchet recursively searches `.` for coverage files. Use `--search-dir` to search a different directory:

```bash
coverageratchet --search-dir coverage check
coverageratchet check --search-dir coverage
```

The flag works in any position. Directories like `.devenv` are automatically skipped to avoid slow traversal of Nix store symlinks.

### Custom config path

```bash
coverageratchet check path/to/my-config.json
coverageratchet ratchet path/to/my-config.json
coverageratchet loosen path/to/my-config.json
coverageratchet baseline-lines path/to/my-config.json
```

## Configuration

CoverageRatchet uses a JSON config file (default: `coverage-ratchet.json` in the current directory).

### Example `coverage-ratchet.json`

```json
{
  "overrides": {
    "Program.fs": {
      "line": 85.5,
      "branch": 77.0,
      "reason": "CLI entry point -- exit calls are not coverable"
    },
    "Api.fs": {
      "line": 92.38,
      "branch": 73.33,
      "reason": "Reflection branches generated by compiler"
    }
  },
  "countFloors": {
    "Api.fs": {
      "coveredLines": 383,
      "coveredBranches": 41,
      "reason": "pooled reports move this file's denominator; the count does not"
    }
  }
}
```

> ### ⚠ `"line"` is a percentage. `"coveredLines"` is a count.
>
> **`"line": 93` means 93 percent of this file's lines. `"coveredLines": 93` means 93 lines.**
>
> In the example above `Api.fs` must stay at **92.38% or better** *and* keep at least **383 covered lines**. Those are two different numbers doing two different jobs, which is why they use different keys in different sections — a percentage floor can never be silently reread as a count, or vice versa.
>
> A config with no `countFloors` section enforces percentages exactly as it always did.

### Config fields

| Field | Type | Description |
|-------|------|-------------|
| `overrides` | object | Per-file threshold overrides, keyed by filename |
| `overrides.<file>.line` | number | Minimum line coverage percentage (0-100) |
| `overrides.<file>.branch` | number | Minimum branch coverage percentage (0-100) |
| `overrides.<file>.reason` | string | Why this file has a lower threshold |
| `overrides.<file>.platform` | string | Optional: `"macos"`, `"linux"`, or `"windows"` — restricts this override to one platform |
| `countFloors` | object | Per-file **covered-count** floors, keyed by filename |
| `countFloors.<file>.coveredLines` | number | Minimum **number of covered lines** — a count, not a percentage |
| `countFloors.<file>.coveredBranches` | number | Minimum **number of covered branches** — a count, not a percentage |
| `countFloors.<file>.reason` | string | Why this file's floor sits where it does |
| `countFloors.<file>.platform` | string | Optional: `"macos"`, `"linux"`, or `"windows"`. Resolved the same way as for `overrides`, but **no command writes one** — see below |

Files not listed in `overrides` must have 100% line and branch coverage.

Files not listed in `countFloors` have **no** count floor — counts are opt-in per file, added by `baseline-lines`.

**A platform-tagged count floor must be written by hand.** `baseline-lines` always writes floors platform-less, and there is no `loosen-from-ci` equivalent for counts: the `coverage-thresholds` artifact carries percentages only. This is deliberate — a floor tagged `macos` is invisible to a Linux-only CI, so nothing tags one on your behalf. If you do hand-write a platform-tagged count floor, remember that every platform without an entry then has *no* count floor for that file, and that you are responsible for keeping a number you cannot measure locally up to date.

## Count floors

Coverage percentage has a denominator problem. The .NET coverage collector emits a source line only when its containing method is **JIT-compiled** during the run, so the set of emitted lines — the percentage denominator — shifts with execution path, machine load, and how many projects you pool together. The numerator (how many lines were actually covered) does not.

The effect is that the same file, with the same tests and the same hits, can report very different percentages:

```
Foo.fs from one project's report:      383 / 412 = 93.0%
Foo.fs pooled with a project that
covers none of it:                     383 / 639 = 59.9%
```

Same hits. Different denominator. A percentage ratchet built on that will fail files nobody touched.

Count floors gate on the number that held still — **383** in both readings:

```bash
# after a full test run, record current counts as floors
coverageratchet baseline-lines

# thereafter, check fails if a file's covered-line count drops
coverageratchet check
```

This catches a regression class percentages structurally cannot see: if a file's emitted-line set shrinks alongside its covered lines, the percentage can stay at a perfect 100% while real coverage is lost.

### The trade-off, stated plainly

A count floor **cannot tell a deleted test from deleted code**. Both lower the count. The only signal that would distinguish them is the total emitted-line count — precisely the number that is not trustworthy — so the tool does not guess.

That means legitimate refactoring (deleting dead code, extracting a module, collapsing duplication) will fail `check`. This is deliberate: those failures land on files you *just changed*, in the same commit, where you can judge them. Re-baselining is one command, and it is the same command used to bootstrap:

```bash
coverageratchet baseline-lines
```

It reports how many floors went **down**, and the lowered floors show up in your config diff for review. Treat a re-baseline as routine, not exceptional.

Run `baseline-lines` against a **full** test run, or with `--merge-baselines`. An impact-filtered partial run covers less code, so its counts are not the file's real counts.

### Platform-specific overrides

When coverage differs across platforms (e.g., OS-specific code paths), a file's override can be an **array** of platform-specific entries instead of a single object:

```json
{
  "overrides": {
    "Program.fs": [
      { "line": 79, "branch": 76, "reason": "CLI entry point", "platform": "macos" },
      { "line": 46, "branch": 44, "reason": "CLI entry point", "platform": "linux" }
    ]
  }
}
```

Resolution rules:
- If an entry matches the current platform, it is used.
- Otherwise, a platform-agnostic entry (no `platform` field) is used as fallback.
- If no entry matches, the file defaults to 100%/100%.

The `loosen` command creates **platform-agnostic** overrides for new files. Only `loosen-from-ci` introduces platform-specific entries, since it integrates coverage results from CI runners on different platforms — and it handles **percentage floors only**. Count floors written by `baseline-lines` are always platform-agnostic.

### Multi-platform workflow

When `loosen-from-ci` writes a single-platform entry (e.g. `linux`), the default 100%/100% threshold still applies to other platforms for that file. Running `check` locally on a platform without an entry will fail — even if actual coverage is high — because 95% < 100%.

The fix is to run `loosen` locally to add the matching platform entry from your actual coverage:

```
# CI (linux) fails on Foo.fs → loosen-from-ci adds { line: 65, branch: 49, platform: linux }
# On your macOS dev machine, `check` now fails because there's no macos entry → default 100%.
coverageratchet loosen coverage-ratchet-<project>.json
# macOS entry added from actual local coverage.
# Later, once tests improve actual coverage on both platforms:
coverageratchet ratchet coverage-ratchet-<project>.json
# Both entries tightened to current numbers.
```

`ratchet` only tightens **existing** entries — it won't synthesize a new platform entry. That's `loosen`'s job. This keeps the split of responsibilities clean: `loosen-from-ci` pins the CI platform at release time, `loosen` pins the dev platform on demand, `ratchet` tightens both as coverage goes up.

## Example Output

`coverageratchet check` groups files into failed and passed, annotating any file that has a lowered threshold:

```
FAILED files:
  FAIL Api.fs: line=90.0% branch=75.0% (3/4 branches) [min: line=92.4% branch=73.3%]
Passed files:
  PASS Program.fs: line=87.2% branch=80.0% [min: line=85.5% branch=77.0%]
  PASS Sync.fs: line=100.0% branch=100.0%

Result: 2/3 files passed
```

## Typical CI Setup

1. Run your tests with coverage enabled (e.g., `dotnet test --collect:"XPlat Code Coverage"`)
2. Run `coverageratchet check` to enforce thresholds
3. Run `coverageratchet` locally after improving tests to lock in coverage gains

## License

MIT

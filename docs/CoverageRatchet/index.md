# CoverageRatchet

Per-file code coverage enforcement that only goes up. CoverageRatchet reads your Cobertura XML coverage reports, compares each file against its threshold, and helps prevent coverage from regressing.

## How It Works

1. Your test suite generates a Cobertura XML coverage report (most .NET coverage tools support this format).
2. CoverageRatchet reads the report and compares each file's line and branch coverage against its threshold.
3. **`check`** fails the build if any file drops below its threshold.
4. **`ratchet`** (the default command) updates thresholds to match current coverage -- thresholds only go up, not down.
5. **`loosen`** sets thresholds to whatever coverage is right now, so `check` passes immediately.
6. **`targets`** lists files sorted by coverage to find improvement opportunities.
7. **`gaps`** shows uncovered branch points per file with line numbers.

The default threshold for every file is **100% line and branch coverage**. Files that can't easily reach 100% (like CLI entry points) can get per-file overrides with a documented reason.

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

Exits with code 0 if all files meet their thresholds, 1 if any file is below.

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

Writes machine-readable coverage results. Exit code matches `check` (non-zero if any file fails). Used by CI workflows to upload coverage data as an artifact.

### Sync thresholds from CI

```bash
coverageratchet loosen-from-ci [config-path]
```

Pushes current code, polls CI, and if coverage fails:
1. Downloads the `coverage-thresholds` artifact
2. Merges CI platform thresholds into local config (splitting non-platform entries if needed)
3. Commits, pushes, and re-polls CI

Requires `gh` CLI and `jj` (or `git`).

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
  }
}
```

### Config fields

| Field | Type | Description |
|-------|------|-------------|
| `overrides` | object | Per-file threshold overrides, keyed by filename |
| `overrides.<file>.line` | number | Minimum line coverage percentage (0-100) |
| `overrides.<file>.branch` | number | Minimum branch coverage percentage (0-100) |
| `overrides.<file>.reason` | string | Why this file has a lower threshold |
| `overrides.<file>.platform` | string | Optional: `"macos"`, `"linux"`, or `"windows"` — restricts this override to one platform |

Files not listed in `overrides` must have 100% line and branch coverage.

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

The `loosen` command creates **platform-agnostic** overrides for new files. Only `loosen-from-ci` introduces platform-specific entries, since it integrates coverage results from CI runners on different platforms.

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

```
  Program.fs: line 87.2% >= 85.5% PASS | branch 80.0% >= 77.0% PASS
  Sync.fs:    line 100%  >= 100%  PASS | branch 100%  >= 100%  PASS
  Api.fs:     line 90.0% >= 92.38% FAIL | branch 75.0% >= 73.33% PASS
```

## Typical CI Setup

1. Run your tests with coverage enabled (e.g., `dotnet test --collect:"XPlat Code Coverage"`)
2. Run `coverageratchet check` to enforce thresholds
3. Run `coverageratchet` locally after improving tests to lock in coverage gains

## License

MIT

# FsSemanticTagger

Automatic semantic versioning for F# NuGet packages. FsSemanticTagger inspects your compiled DLL to detect API changes and determines the correct version bump according to [Semantic Versioning](https://semver.org/).

> **Status: early alpha, and substantially AI-written.** Runs the author's own F# OSS repos daily, but behavior and flags shift between versions and rough edges are expected — your mileage may vary. Issues and PRs welcome.

## How It Works

FsSemanticTagger extracts the public API surface from your compiled assembly using reflection metadata. It compares the current API against the previously released version and classifies changes:

| Change type | Example | Version bump |
|------------|---------|-------------|
| **Breaking** (removed/changed) | Deleted a public method | Major (or minor if < 1.0) |
| **Addition** (new public API) | Added a new public type | Minor (or patch if < 1.0) |
| **No API change** | Internal refactoring | Patch |

## Installation

```bash
dotnet tool install -g FsSemanticTagger
```

## Usage

### Generate a config file

For monorepos with multiple packages, generate a `semantic-tagger.json` config:

```bash
fssemantictagger init
```

This scans for packable `.fsproj` files and writes a config with sensible defaults. For single-package repos, the tag prefix is `"v"`. For multi-package repos, each package gets a prefix like `"mylib-v"`.

### Extract the public API from a DLL

```bash
fssemantictagger extract-api path/to/MyLib.dll
```

Outputs one signature per line:

```
type MyNamespace.MyClass
  MyClass::MyMethod(int, string): bool
  MyClass::MyProperty: string
  MyClass::.ctor(int)
```

### Compare two versions of a DLL

```bash
fssemantictagger check-api old/MyLib.dll new/MyLib.dll
```

Exit codes:
- **0** -- No API changes
- **1** -- Non-breaking additions only
- **2** -- Breaking changes detected

### Orchestrate a release

```bash
# Auto-detect changes and bump version accordingly
fssemantictagger release

# Start a pre-release cycle
fssemantictagger alpha
fssemantictagger beta
fssemantictagger rc

# Promote to stable
fssemantictagger stable
```

The `release` command:
1. Checks for a clean working copy (no uncommitted changes)
2. **Fail-fast CI precondition:** confirms the release commit is on the remote and waits for its CI to go green — *before* the expensive build / coverage reconciliation. If the commit isn't pushed yet, it stops in ~1 second with an actionable "push first (or pass `--push`)" message instead of running the whole local CI and only then failing. (See [Fail-fast CI precondition](#fail-fast-ci-precondition).)
3. Reconciles local coverage floors from the green CI run's coverage artifact (when `coverageratchet` is a local tool)
4. Builds in Release configuration
5. Compares API against the previous release tag
6. Validates each bumped package's `CHANGELOG.md` has a non-empty `## Unreleased` section
7. Updates the version in your `.fsproj` file(s)
8. Promotes the `## Unreleased` section to `## <version> - YYYY-MM-DD` and inserts a fresh empty `## Unreleased` above it
9. Creates a VCS tag (supports both Git and [Jujutsu](https://jj-vcs.github.io/jj/))

### Fail-fast CI precondition

Releasing needs the release commit's **CI coverage artifact** (to reconcile the Linux-CI vs local coverage floors via `coverageratchet loosen-from-ci`), so the commit must be pushed and its CI must finish first. The tool checks this up front and reacts honestly:

| Release commit state | What happens |
|---|---|
| Not on the remote (not pushed) | **Fail fast in ~1 second** with: *"the release commit isn't on the remote … Push the branch and wait for CI, then re-run — or pass `--push`."* It is never mislabelled as a CI failure. |
| Pushed, CI queued / running | **Waits** (polls) for the run to finish — you don't hand-roll a `gh run watch` loop. |
| Pushed, CI passed | Proceeds. |
| Pushed, CI failed | Errors *"CI failed for the release commit"* and names the failing run's URL — the real failure case, kept distinct from "not pushed". |

Pass `--push` to opt into auto-pushing the commit (then the tool waits for its CI). Auto-push is **off by default** because pushing to a branch-protected / PR-gated `main` is unsafe to do implicitly.

### Changelog promotion

For each package being bumped the tool promotes its `CHANGELOG.md`'s `## Unreleased` section to a versioned header of the form:

```
## <version> - YYYY-MM-DD
```

A fresh empty `## Unreleased` heading is inserted above it so the file is ready for the next cycle. Both `## Unreleased` and `## [Unreleased]` are recognized (case-insensitive); the re-inserted heading is always unbracketed. The changelog edit lands in the same "Bump versions: ..." commit as the fsproj version update.

**Changelog location:**
- **Single-package repos** (one packable fsproj): `CHANGELOG.md` at the repo root.
- **Multi-package repos**: `CHANGELOG.md` next to each package's fsproj (and next to each path in `fsProjsSharingSameTag`).

**Fail-fast:** if any package needing a bump is missing `CHANGELOG.md`, is missing the `## Unreleased` section, or the section is empty, the release aborts with exit code 1 before any files are modified.

### Flags

All release commands (`release`, `alpha`, `beta`, `rc`, `stable`) accept:

- `--dry-run` — preview version bumps without modifying files or creating tags. Skips the clean-working-copy and CI checks; still builds and compares APIs so the preview is accurate. Missing or empty `## Unreleased` sections are reported as warnings instead of aborting.
- `--publish` — build and pack locally (`dotnet pack -c Release -o artifacts/`) instead of pushing tags for CI to publish.
- `--skip-nuget-wait` — after pushing tags, exit immediately instead of polling NuGet until the published package(s) are restorable. By default the command waits for the new version(s) to be indexed; this poll never changes the exit code (a timeout warns and still exits 0).
- `--only <names>` — restrict the run to specific package(s) by name (comma-separated; e.g. `--only Foo,Bar`). Names match the `name` field of entries in `semantic-tagger.json`. When omitted, **all** packages are processed (the default). Only the selected packages are considered for version computation and tagging; the rest are out of scope entirely (not bumped, not tagged, not even reported as "skipped"). An unknown name aborts with exit code 1 and lists the valid names — it never silently no-ops.
- `--push` — if the release commit isn't on the remote yet, push it and wait for its CI to finish, then proceed. The default is to **fail fast** with a "push first" message rather than push implicitly (unsafe on a branch-protected / PR-gated `main`). A commit that *is* already pushed is always waited on regardless of this flag. See [Fail-fast CI precondition](#fail-fast-ci-precondition).

```bash
# Preview what would be released
fssemantictagger release --dry-run

# Local build-and-pack instead of CI release
fssemantictagger release --publish

# Push tags but don't wait for NuGet to index the release
fssemantictagger release --skip-nuget-wait

# Release only the named package(s), ignoring the rest of the config
fssemantictagger release --only TestPrune.Analyzers
fssemantictagger release --only Foo,Bar --dry-run

# Normal release after you've pushed the commit: the tool waits for CI itself,
# so you never hand-roll a `gh run watch` loop. If the commit ISN'T pushed it
# fails fast in ~1s with an actionable message instead of running full local CI.
fssemantictagger release

# Push the release commit and wait for its CI, then release (one shot)
fssemantictagger release --push
```

## Configuration

FsSemanticTagger works with zero configuration for simple projects. It auto-discovers a single packable `.fsproj` file (one with a `<PackageId>` element).

For monorepos or custom setups, create a `semantic-tagger.json`:

```json
{
  "packages": [
    {
      "name": "MyLib",
      "fsproj": "src/MyLib/MyLib.fsproj",
      "tagPrefix": "v",
      "fsProjsSharingSameTag": ["src/MyLib.Extensions/MyLib.Extensions.fsproj"]
    }
  ],
  "reservedVersions": ["1.0.0"]
}
```

### Config fields

| Field | Type | Description |
|-------|------|-------------|
| `packages` | array | List of packages to manage |
| `packages[].name` | string | Package name |
| `packages[].fsproj` | string | Path to the project file |
| `packages[].dllPath` | string? | Path to compiled DLL (auto-derived if omitted) |
| `packages[].tagPrefix` | string? | Git/jj tag prefix (default: `"v"`) |
| `packages[].fsProjsSharingSameTag` | string[]? | Other `.fsproj` files to update with the same version |
| `reservedVersions` | string[]? | Versions to skip |
| `preBuildCmds` | string[]? | Commands to run before the build that produces each DLL |

## Pre-release Version Flow

FsSemanticTagger follows a structured pre-release progression:

```
0.1.0-alpha.1  ->  0.1.0-alpha.2  ->  0.1.0-beta.1  ->  0.1.0-rc.1  ->  0.1.0
```

- `alpha` / `beta` increments: bumps the pre-release number (e.g., `alpha.1` to `alpha.2`)
- If API changes are detected during `rc`, it drops back to `beta`
- `stable` removes the pre-release suffix

## VCS Support

FsSemanticTagger works with both Git and [Jujutsu](https://jj-vcs.github.io/jj/). It tries Jujutsu first, then falls back to Git. Tags and clean-working-copy checks work with either VCS.

## License

MIT

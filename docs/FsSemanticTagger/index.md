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

### What the changelog declares is a floor

The API diff cannot see every breaking change. A `[<Literal>]` constant is inlined into each consumer when it compiles, so changing its value breaks consumers built against the old one, but the public-API dump does not include the value.

So in `auto` mode the release also reads the entries it is about to publish: the authored `## Unreleased` section, or the entries derived from commit summaries when that section is empty (see [Changelog promotion](#changelog-promotion)). Each entry is checked for a conventional-commit marker at its start:

| Entry | Declares | Effect on the bump |
|-------|----------|--------------------|
| `feat!:`, `fix!:`, any `<type>!:`, or `BREAKING CHANGE:` | a breaking change | at least Major (or minor if < 1.0) |
| `feat:` | a feature | at least Minor (or patch if < 1.0) |
| `fix:`, `chore:`, other recognised types | a patch-level change | no floor |

The bump is the stronger of the declared and the computed change. When the two disagree the release prints a line saying so, in either direction: when the changelog raised the bump, and when the API diff found more than the changelog declares. A changelog with no markers leaves the bump exactly as the API diff computes it. With several changelogs behind one tag (`fsProjsSharingSameTag`), the strongest declaration across them counts. Markers inside fenced code blocks are ignored.

Diffing literal values was considered and not done: it would major-bump every package whose internal constants change (build stamps, thresholds, retry counts) to catch only an author who forgot to declare the break.

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

**What gets promoted** (`release --check` prints exactly this, from the same plan):

- **An authored `## Unreleased` section is promoted as written.** Commit summaries are *not* merged into it: the tool cannot tell which commits an authored entry already describes, so covering the rest of the release is the author's job.
- **An empty or missing section is derived** from the summary lines of the commits since the package's last tag that touch its source (or bundled-dependency) directories, grouped breaking → feat → fix → other.
- **Either way, consumer-visible dependency changes are recorded.** A `<PackageReference Include=... Version=...>` added, removed or moved to another version since the last tag, in any of the package's fsprojs, gets a bullet such as `- build(deps): bump SqlHydra.Query from 4.1.0-beta.2 to 4.1.0-beta.3` after the section's entries — unless the section already names the package and its new version. These come from the fsproj itself, never from commit prose. `PrivateAssets="all"` (build-only) references, `Update` items and versionless (Central Package Management) references are not tracked.

**Fail-fast:** if a package needing a bump has an unauthored section and nothing to derive one from (no prior tag, no qualifying commits and no dependency changes), the release aborts with exit code 1 before any files are modified.

### Callout order

A **callout** is a blockquote that opens with a heading or a GitHub alert marker — the "read this first" banner:

```markdown
## Unreleased

> ### ⚠️ Read this first if you run the CLI from a script
>
> Two exit codes changed.

- feat: ...
```

A callout must be the **first content** of `## Unreleased`. A merge that keeps both sides of an `## Unreleased` conflict prepends the incoming entries *above* it, so the one block whose job is to be read first is no longer read first — with no conflict marker, no duplicate and no empty section to give it away. That is a structural fact about the section, so it is checked structurally: the rule keys on markdown shape and never on prose.

The rule is deliberately narrow, and has no opt-out:

- Only a blockquote opening with an ATX heading (`> ### ...`) or an alert marker (`> [!WARNING]`, `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!CAUTION]`) counts. A plain `> quoted line` — an aside, some quoted output — is ignored wherever it sits.
- Blockquotes inside fenced code are sample text, so a changelog documenting callout syntax doesn't fail on its own example.
- Only `## Unreleased` is checked. Released sections are history.
- Checked files are every package's `CHANGELOG.md` plus the repo-root `CHANGELOG.md` when one exists — in a multi-package repo the root file is the reader-facing aggregate, and it is exactly where a callout lives.

Both `release` (before any writes) and `release --check` enforce it. Unlike an empty section, a buried callout is never excused by "derivable from commits": deriving bullets cannot move a callout back to the top, and promoting the section would freeze the wrong order into a published version.

### `--check`

```bash
fssemantictagger release --check
```

A pre-flight gate for CI (`mise run changelog-check`). It never builds, diffs APIs or writes anything. For every package with source changes since its last tag it prints what release will promote — the authored section, or the derived entries, plus any dependency-change bullets (see [Changelog promotion](#changelog-promotion)). It reads the same plan the release applies, so what it prints is what gets written.

A pass does **not** certify that an authored section covers every commit; it certifies that there is something to promote and that consumer-visible dependency changes are recorded. It fails with exit code 1 when either

- a package with source changes since its last tag has an empty/missing `## Unreleased` and nothing to derive one from (no commit summaries, no dependency changes), or
- a `## Unreleased` callout is no longer its section's first content.

### Flags

All release commands (`release`, `alpha`, `beta`, `rc`, `stable`) accept:

- `--dry-run` — preview version bumps without modifying files or creating tags. Skips the clean-working-copy and CI checks; still builds and compares APIs so the preview is accurate. Missing or empty `## Unreleased` sections are reported as warnings instead of aborting.
- `--publish` — build and pack locally (`dotnet pack -c Release -o artifacts/`) instead of pushing tags for CI to publish.
- `--skip-nuget-wait` — after pushing tags, exit immediately instead of polling NuGet until the published package(s) are restorable. By default the command waits up to **20 minutes** (81 checks, 15s apart) for the new version(s) to be indexed — NuGet's index typically lags the Release run by 6-15 minutes. If the poll gives up it **exits 2**, prints the wait it actually performed, and says so in terms that are not a failed publish: the tags are pushed and each has a Release run; re-running the same release command resumes rather than re-publishes. Override the budget with `FSHW_NUGET_PROBE_ATTEMPTS` / `FSHW_NUGET_PROBE_DELAY_MS` (the same variables FsHotWatch's release barrier reads). In a release where one package depends on another, this flag skips only the confirmation of the last wave: the wait between a dependency and its dependents is never skipped (see [Publication order](#publication-order)).
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

### Publication order

Each package is published by its own tag, and each tag starts its own Release run, so nothing in CI orders them. When a release includes a package and a separately released package it references (directly or through other projects' `<ProjectReference>`s), `release` publishes in **waves**:

1. The tags of every package with no dependency in this release are pushed, and each tag's publish run is confirmed.
2. The exact version of each of those packages must be **on NuGet** before any tag of the next wave is pushed.
3. The next wave is the packages whose dependencies are now all published, and so on.

The order comes from the `<ProjectReference>` graph on disk, not from the order of `packages` in `semantic-tagger.json`: a CLI listed before a library it references is still published after it. Packages that do not depend on each other share a wave and are pushed in config order. A dependency that is not part of this release adds no wait, but a dependency reached through it still does. `--dry-run` prints the waves when there is more than one.

The graph is checked before anything is written. `release` refuses to start (exit **1**) when packages depend on each other in a cycle, when one fsproj belongs to two packages (as a package's `fsproj` or in `fsProjsSharingSameTag`), or when two packages share a name.

If a dependency does not reach NuGet in time, its dependents' tags are **not pushed** and the release exits **2**. The tags exist locally; running the same release command again resumes and pushes them once the dependency is published.

### Post-push waits and their exit codes

After the tags are pushed, `release` confirms each one triggered a run of a publish workflow (`publishWorkflows`, default `.github/workflows/release.yml`) and then waits for NuGet. Both waits are bounded and both distinguish "not yet" from "not happening":

| Wait | Default budget | Override | On give-up |
|------|----------------|----------|------------|
| A publish workflow run for each tag | 10 min (121 asks, 5s apart) | `FSST_RUN_POLL_ATTEMPTS`, `FSST_RUN_POLL_DELAY_MS` | exit **2** — the tag IS pushed, the run may still be starting; do **not** delete and re-push the tag (that publishes twice). A run that exists and has already failed is exit **1**. |
| The package(s) on NuGet | 20 min (81 checks, 15s apart) | `FSHW_NUGET_PROBE_ATTEMPTS`, `FSHW_NUGET_PROBE_DELAY_MS` | exit **2** — the tags and their Release runs are the evidence of the publish; re-running the same release command resumes instead of re-publishing. |

Exit `0` means every tag has a run and (unless `--skip-nuget-wait`) every package is on the feed. Exit `1` is reserved for a release that demonstrably did not happen: CI red, a tag push that failed, or a publish run that finished without publishing.

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
| `publishWorkflows` | string[]? | Paths of the workflows whose tag-triggered run publishes a package (default: `[".github/workflows/release.yml"]`). After pushing a tag, only runs of these workflows are consulted; a run of any other workflow the tag triggered (a docs deploy, say) cannot refuse the release. Must not be empty. |

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

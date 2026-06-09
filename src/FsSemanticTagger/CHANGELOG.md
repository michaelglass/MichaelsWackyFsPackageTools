# Changelog

## Unreleased

- feat: dependency-aware version bump. A package's change-detection now also considers its transitive `<ProjectReference>` closure (auto-derived from the fsproj — no config field). A bundling package (e.g. a `PackAsTool` CLI that physically ships its referenced DLLs) is now re-released ("rebundle" bump) when a bundled dependency's source changes since its last tag, even if the package's own source is unchanged. A rebundle is a `NoChange`-style bump that skips API extraction (a bundled tool/exe has no meaningful public API) and, when the package's own `## Unreleased` section is missing or empty, auto-inserts a `- chore: rebuild to bundle updated dependencies` changelog entry. Normal own-source bumps keep the strict changelog validation and API-diff behavior unchanged.

## 0.13.0-alpha.8 - 2026-06-03

- feat: `release`/`alpha`/`beta`/`rc`/`stable` now **resume a bumped-but-untagged release** on re-run. If a package's fsproj `<Version>` is ahead of its latest tag and no tag exists at that version (e.g. a prior run bumped the version and rolled the changelog but the tag push failed because CI flaked), the tool detects the in-progress release, verifies the commit's CI is green, and pushes the missing tag — instead of reporting "No packages to release". Decided off desired end-state (version vs tag), not work-remaining, so a mid-release failure self-heals on the next run.
- feat: add `--only <names>` to `release`/`alpha`/`beta`/`rc`/`stable` (and `--dry-run`) to scope a run to specific package(s) by name (comma-separated; names match the `name` field in `semantic-tagger.json`). When omitted, all packages are processed as before. Scoped runs only consider the selected packages for version computation and tagging; the rest are out of scope entirely. An unknown name aborts with a clear error listing the valid names instead of silently no-opping.
- chore: bump CommandTree 0.6.1 -> 0.6.2 (revision-stamping target fix; no behavior change).

## 0.13.0-alpha.7 - 2026-06-02

- feat: after pushing tags in the default PushTags mode, `release`/`alpha`/`beta`/`rc`/`stable` now poll NuGet until each newly-released package version is restorable (indexed) before exiting, so the command only returns once the release is actually live. The poll never changes the exit code — tags are already pushed, so a timeout prints a warning and still exits 0. Pass `--skip-nuget-wait` to exit immediately after pushing tags instead.
- feat: add a `--version` flag that prints the installed tool version.
- fix: invalid CLI arguments now print a readable error message instead of the raw parser output.
- chore: bump CommandTree 0.5.1 → 0.6.1.

## 0.13.0-alpha.6 - 2026-05-28

- chore: bump CommandTree 0.5.0 → 0.5.1.

## 0.13.0-alpha.5 - 2026-05-28

- fix: `release` no longer crashes when diffing against a previously-published package whose public API references external dependencies (e.g. `Falco`). The prior release's assembly is loaded from the NuGet cache lib dir, which has no co-located `.deps.json`, so transitive dependency assemblies were never added to the `MetadataLoadContext` resolver and reading any dependency-referencing type threw `FileNotFoundException`. The resolver now walks the package's `.nuspec` dependency graph to resolve those lib dirs, and assembly extraction degrades to "couldn't read the previous API" instead of crashing.

## 0.13.0-alpha.4 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300, System.Reflection.MetadataLoadContext 10.0.5 -> 10.0.8

## 0.13.0-alpha.3 - 2026-05-26

- fix: `release` no longer ships a breaking change as a patch when the previous release's package isn't in the local NuGet cache. It now downloads the prior package to read its API (honoring the repo's `nuget.config` via `--configfile`), and if the previous API still can't be obtained it aborts loudly instead of silently assuming "no change".

## 0.13.0-alpha.2 - 2026-05-04

- fix: `--publish` (LocalPublish) mode no longer creates jj tags that are never pushed — tags are now only created in PushTags mode, preventing "no changes since <unpushed-tag>" false-skips on subsequent runs

## 0.13.0-alpha.1 - 2026-04-27

- feat: subcommand `--help` now emits per-command details (e.g. `fssemantictagger release --help` explains what `release`/`alpha`/`beta`/`rc`/`stable` do and what `--dry-run` / `--publish` mean)
- feat: top-level `--help` documents the `semantic-tagger.json` schema and shows examples
- feat: accept `-h` and `help` as aliases for `--help`

## 0.12.0-alpha.5 - 2026-04-24

- feat: `--dry-run` flag on `release`/`alpha`/`beta`/`rc`/`stable` previews version bumps without modifying files, creating tags, or running the clean-working-copy and CI checks. Missing or empty `## Unreleased` sections report as warnings instead of aborting.
- **Breaking:** CLI flags are now named (`--publish`, `--dry-run`) rather than positional booleans. Callers passing `release true` must switch to `release --publish`.
- fix: print error message when `coverageratchet loosen-from-ci` fails instead of silently exiting with code 1
- fix: `Shell.run` now falls back to stdout when stderr is empty on failure, so diagnostic messages from tools that write to stdout are not lost

## 0.12.0-alpha.4 - 2026-04-22

- feat: promote `## Unreleased` section to `## <version> - YYYY-MM-DD` on release, inserting a fresh empty `## Unreleased` above it. Single-package repos use repo-root `CHANGELOG.md`; multi-package repos use `CHANGELOG.md` next to each fsproj (and each `fsProjsSharingSameTag`). Release aborts fail-fast (exit 1, no writes) if any required CHANGELOG.md is missing, has no `## Unreleased` section, or the section is empty.
- **Breaking:** `Config.ToolConfig` gains a `RootDir: string` field (populated by `Config.load`). Callers constructing the record directly must supply it.

## 0.12.0-alpha.3 - 2026-04-20

- fix: `hasChangesSinceTag` always returned true — `jj diff --stat` outputs summary text even for zero changes; use `--summary` instead (empty when no changes, compact file list otherwise)

## 0.12.0-alpha.2 - 2026-04-15

- feat: idempotent release — detect already-bumped fsproj versions on retry, skip commit/push, resume from CI polling
- feat: return exit code 1 on post-push CI failure/timeout (was incorrectly returning 0)
- feat: bump CI polling timeout from 10 to 15 minutes
- refactor: extract `waitForCiAndPushTags` and `packLocally` helpers
- refactor: add `BumpDecision` type (`NeedsBump`/`AlreadyBumped`) and `readFsprojVersion` function
- refactor: share compiled `versionElementRegex` between read/update functions
- fix: trivially-true test assertion in CI failure test

## 0.12.0-alpha.1

- refactor: type-driven design — add `RunStatus`/`RunConclusion` DUs (replacing raw strings in `CiRunInfo`), `ApiChange` uses non-empty list pattern `head * rest`, `Version.tryParse` returns `Result` instead of throwing, `Config.load`/`discover` return `Result` instead of throwing, `HasPreviousRelease` drops redundant `tag` field
- fix: `withJjGitDir` now uses `resolveGitDir` with absolute paths and `.git` pre-check (matching CoverageRatchet fix)
- chore: bump CommandTree dependency to 0.4.0

## 0.10.0-alpha.1

- fix: bump CommandTree to 0.3.5, restore ReleaseOptions record (record-typed args with defaults now supported)

## 0.9.0-alpha.1

- feat: integrate `loosen-from-ci` into release workflow — automatically loosens coverage thresholds from CI before version bumps
- fix: loosen FsSemanticTagger Release.fs thresholds with per-platform entries
- style: format ReleaseTests.fs with Fantomas
- chore: update NuGet dependencies

## 0.8.0-alpha.4

- feat: wait for CI on version bump commit before pushing tags
- fix: lower Release.fs line coverage threshold to 93%

## 0.8.0-alpha.3

- refactor: remove dead `tagLastCommit`, extract TFM list, simplify reserved version check
- fix: push tags individually after main to trigger GitHub Actions
- fix: lower Vcs.fs Linux branch coverage threshold

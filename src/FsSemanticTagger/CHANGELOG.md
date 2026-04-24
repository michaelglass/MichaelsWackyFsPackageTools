# Changelog

## Unreleased

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

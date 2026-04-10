# Changelog

## Unreleased

- refactor: type-driven design — add `RunStatus`/`RunConclusion` DUs (replacing raw strings in `CiRunInfo`), `ApiChange` uses non-empty list pattern `head * rest`, `Version.tryParse` returns `Result` instead of throwing, `Config.load`/`discover` return `Result` instead of throwing, `HasPreviousRelease` drops redundant `tag` field
- fix: `withJjGitDir` now uses `resolveGitDir` with absolute paths and `.git` pre-check (matching CoverageRatchet fix)

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

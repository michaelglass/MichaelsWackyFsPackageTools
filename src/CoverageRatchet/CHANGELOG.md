# Changelog

## Unreleased

- fix: `loosen` command now creates platform-agnostic overrides for new files (only `loosen-from-ci` introduces platform-specific entries)
- feat: add `targets` command — list files sorted by coverage, lowest first
- feat: add `gaps` command — show uncovered branch points per file
- feat: support multiple XML files for ratchet/check/loosen commands
- feat: expose `RawLine`, `extractRawLines`, `buildCoverage` as public API for consumer use
- feat: add `BranchGap`/`FileBranchGaps` types and `buildBranchGaps` function for branch gap analysis
- fix: `loosen-from-ci` now works in jujutsu (jj) repos — detects `.jj/repo/store/git` and sets `GIT_DIR` with absolute path; checks `.git` first for normal git repos
- refactor: type-driven design — add `Platform` DU (replacing raw strings), `CiFileResult` record, `FileResult` module with computed `passed`/`linePassed`/`branchPassed`, `CoverageFileCommand` DU eliminating `failwith "unreachable"`, `CheckResult` now uses `RatchetStatus.NoChanges` without payload, `Override.Reason` is `string option`
- refactor: DI for `pollCi`, `getVcsSha`, `vcsPush`, `vcsCommitAndPush` — accept injected `run` parameter for testability
- chore: bump CommandTree dependency from 0.3.3 to 0.4.0
- chore: remove FSharp.Core version pin to avoid forcing a specific version on consumers

## 0.10.0-alpha.1

- Version bump only, no CoverageRatchet-specific changes

## 0.9.0-alpha.1

- feat: add `loosen-from-ci` command — merges coverage thresholds from CI artifacts with per-platform entries
- fix: make mergeFromCi test platform-independent
- fix: loosen CoverageRatchet thresholds with per-platform entries for new code
- style: format Shell.fs, Ratchet.fs with Fantomas
- chore: sync CoverageRatchet docs
- chore: update NuGet dependencies

## 0.8.0-alpha.4

- Version bump only

## 0.8.0-alpha.3

- Version bump only

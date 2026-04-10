# Changelog

## Unreleased

- Bump CommandTree dependency from 0.3.3 to 0.3.5 (catching up to FsSemanticTagger)
- refactor: type-driven design — add `Platform` DU (replacing raw strings), `CiFileResult` record, `FileResult` module with computed `passed`/`linePassed`/`branchPassed`, `CoverageFileCommand` DU eliminating `failwith "unreachable"`, `CheckResult` now uses `RatchetStatus.NoChanges` without payload, `Override.Reason` is `string option`
- fix: `loosen-from-ci` now works in jujutsu (jj) repos — detects `.jj/repo/store/git` and sets `GIT_DIR` with absolute path; checks `.git` first for normal git repos
- refactor: DI for `pollCi`, `getVcsSha`, `vcsPush`, `vcsCommitAndPush` — accept injected `run` parameter for testability

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

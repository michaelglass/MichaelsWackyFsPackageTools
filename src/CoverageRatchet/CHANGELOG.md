# Changelog

## Unreleased

- fix: `loosen-from-ci` (and all `gh`-backed CI polling) now resolves the git store by walking up from any nested subdirectory, not just the repo root. The coverage tasks `cd` into the per-project `coverage/<Project>/` dir before invoking the tool; previously `resolveGitDir` only inspected that single directory, found no `.jj`/`.git`, and gave up — so `GIT_DIR` was never set and `gh` polling failed. It now ascends to the first ancestor that is a repo root (jj or native git), so the tool works from wherever it is launched.
- deps: bump CommandTree 0.6.2 -> 0.6.3.

## 0.15.0-alpha.7 - 2026-06-10

- fix: `loosen-from-ci` (and all `gh`-backed CI polling) now works from a jj secondary workspace — `resolveGitDir` follows the `.jj/repo` pointer file to the real git store instead of returning nothing when `.jj/repo` is not a directory.

## 0.15.0-alpha.6 - 2026-06-03

- fix: `loosen-from-ci` now only **lowers** coverage floors, never raises them (picks up the `CoverageRatchet.Core` `mergeFromCi` fix) — a transiently-higher CI measurement no longer raises a floor above what CI stably hits, which had made the ratchet anti-converging on noisy CI runners.
- chore: bump CommandTree 0.6.1 -> 0.6.2 (revision-stamping target fix; no behavior change).

## 0.15.0-alpha.5 - 2026-06-02

- feat: add a `--version` flag that prints the installed tool version.
- fix: invalid CLI arguments now print a readable error message instead of the raw parser output.
- chore: bump CommandTree 0.5.1 → 0.6.1.

## 0.15.0-alpha.4 - 2026-05-28

- chore: bump CommandTree 0.5.0 → 0.5.1.

## 0.15.0-alpha.3 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300

## 0.15.0-alpha.2 - 2026-05-26

- docs: document the upstream-package coverage-exclusion pattern

## 0.15.0-alpha.1 - 2026-05-05

- refactor: extract Cobertura parsing, threshold checking, ratcheting, and merging into CoverageRatchet.Core; CoverageRatchet CLI delegates to the shared library

## 0.14.0-alpha.2 - 2026-05-04

- fix: vcsPush now handles jj "has no description" error by parsing rejected bookmark names from hint lines and retrying with explicit --bookmark flags instead of failing

## 0.14.0-alpha.1 - 2026-04-27

- feat: subcommand `--help` now works (e.g. `coverageratchet merge --help`) and emits per-command details — argument descriptions, when to use it, and where applicable an example invocation
- feat: top-level `--help` documents global flags (`--search-dir`, `--merge-baselines`), the full `coverage-ratchet.json` schema (including platform-specific overrides), and example invocations
- feat: accept `-h` and `help` as aliases for `--help`

## 0.13.0-alpha.3 - 2026-04-24

- feat: add `merge` command — take two Cobertura XMLs, produce one with max hits per line (union classes/methods/conditions, rates recomputed)
- feat: add `check --merge-baselines` flag — before checking, layer each `coverage.cobertura.xml` onto a sibling `coverage.baseline.xml`. Partial (impact-filtered) test runs can raise coverage but never lower it. Bootstraps a missing baseline from the current run.
- feat: add `refresh-baseline` command — advance each project's baseline to match the current coverage. Use after a deliberate full test run.
- feat: `check --merge-baselines` auto-refreshes baselines when the `FSHW_RAN_FULL_SUITE=true` env var is set AND the check passes, so fs-hot-watch-driven full runs don't need a separate refresh step.
- internal: `mergeIntoBaselines` wraps per-project errors with the project directory, so corrupt-baseline failures identify which project needs attention

## 0.13.0-alpha.2 - 2026-04-22

- test: raise Program.fs coverage via DI seams; add default test timeouts
- docs: attribute CHANGELOG entries to released versions

## 0.13.0-alpha.1 - 2026-04-17

- fix: harden `loosen-from-ci` against empty/missing threshold artifacts

## 0.12.0-alpha.2 - 2026-04-15

- Version bump only, no CoverageRatchet-specific changes

## 0.12.0-alpha.1 - 2026-04-13

- feat: add `--search-dir <dir>` flag to search a specific directory instead of `.` (works in any position)
- fix: skip `.devenv` directories during coverage file search to avoid hanging on Nix store symlinks

## 0.11.0-alpha.1 - 2026-04-13

- fix: `loosen` command now creates platform-agnostic overrides for new files (only `loosen-from-ci` introduces platform-specific entries)
- feat: add `targets` command — list files sorted by coverage, lowest first
- feat: add `gaps` command — show uncovered branch points per file
- feat: support multiple XML files for ratchet/check/loosen commands
- feat: expose `RawLine`, `extractRawLines`, `buildCoverage` as public API for consumer use
- feat: add `BranchGap`/`FileBranchGaps` types and `buildBranchGaps` function for branch gap analysis
- fix: `loosen-from-ci` now works in jujutsu (jj) repos — detects `.jj/repo/store/git` and sets `GIT_DIR` with absolute path; checks `.git` first for normal git repos
- chore: bump CommandTree dependency from 0.3.3 to 0.4.0
- chore: remove FSharp.Core version pin to avoid forcing a specific version on consumers

## 0.10.0-alpha.2 - 2026-04-11

- refactor: type-driven design — add `Platform` DU (replacing raw strings), `CiFileResult` record, `FileResult` module with computed `passed`/`linePassed`/`branchPassed`, `CoverageFileCommand` DU eliminating `failwith "unreachable"`, `CheckResult` now uses `RatchetStatus.NoChanges` without payload, `Override.Reason` is `string option`
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

# Changelog

## Unreleased

## 0.10.0-alpha.12 - 2026-07-23

- feat: new repo-level check "Local packs are ref-stamped (RefStamp)" (AUTOMATION-123) — packable repos must wire in the RefStamp MSBuild guard so a local `dotnet pack` derives its version from the jj/git source ref and cannot produce a release-shaped version. Satisfied by a `<PackageReference Include="RefStamp" PrivateAssets="all" />` in a root `Directory.Build.props`/`Directory.Build.targets` (one line, repo-wide), by the same reference in every packable fsproj, or by a direct `<Import>` of `RefStamp.targets` (the shape RefStamp's own monorepo uses). Repos with no packable projects are unaffected.

## 0.10.0-alpha.11 - 2026-06-13

- change: the "No gitignored files in git history" check now scans only the **current branch's** ancestry, not `--branches --remotes`. A gitignored file that leaks only on an unrelated experiment branch no longer fails the gate on `main` — you only care about the history you'll publish from the branch you're on. The current commit is resolved per-repo: for jj-backed repos via `jj log --no-graph --ignore-working-copy -r @- -T commit_id` (git `HEAD` is unreliable under jj — it points at `refs/jj/root` or a stale detached commit, not the branch you're on), and for plain-git repos via `HEAD`. When no current commit resolves (unborn branch / jj root) the check passes. The tracked-vs-history-only split and `git check-ignore --no-index` filtering are unchanged.

## 0.10.0-alpha.10 - 2026-06-13

- feat: new repo-level check "No gitignored files in git history" — fails when any file matching the repo's `.gitignore` was ever committed (currently tracked or history-only), so gitignore leaks into the published history are caught and fixed (untrack for current, history rewrite for history-only). Default and flagless like every other check. Uses a single efficient pass (`git log --branches --remotes --diff-filter=A --name-only` ∩ `git check-ignore --no-index`) over the resolved git store, works for both jj-backed and plain-git repos, and passes when the directory is not a repo. This supersedes the bespoke `scripts/check-gitignore-leaks.fsx` for detection (the script's `--fix` untrack helper stays for remediation).

## 0.10.0-alpha.9 - 2026-06-12

- deps: bump CommandTree 0.6.2 -> 0.6.3.

## 0.10.0-alpha.8 - 2026-06-10

- chore: float the FSharp.Core pin to `10.1.*` (was literal `10.1.300`; newer SDKs imply `10.1.301`, which tripped NU1605 on CI). No behavior change.

## 0.10.0-alpha.7 - 2026-06-03

- chore: bump CommandTree 0.6.1 -> 0.6.2 (revision-stamping target fix; no behavior change).

## 0.10.0-alpha.6 - 2026-06-02

- feat: add a `--version` flag that prints the installed tool version.
- fix: invalid CLI arguments now print a readable error message instead of the raw parser output.
- chore: bump CommandTree 0.5.1 → 0.6.1.

## 0.10.0-alpha.5 - 2026-05-28

- chore: bump CommandTree 0.5.0 → 0.5.1.

## 0.10.0-alpha.4 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300

## 0.10.0-alpha.3 - 2026-05-26

- chore: align FSharp.Core to 10.1.300 (matches the .NET 10 SDK; resolves an NU1605 downgrade in the test projects)

## 0.10.0-alpha.2 - 2026-05-04

- chore: bump CommandTree to 0.5.0; update FSharp.Core to 10.1.203

## 0.10.0-alpha.1 - 2026-04-27

- feat: top-level `--help` now lists every repo-level and project-level check fsprojlint runs, so users know what's being validated without reading the source
- feat: accept `-h` and `help` as aliases for `--help`

## 0.9.0-alpha.3 - 2026-04-22

- docs: attribute CHANGELOG entries to released versions

## 0.9.0-alpha.2 - 2026-04-15

- Version bump only

## 0.9.0-alpha.1 - 2026-04-13

- Version bump only

## 0.8.0-alpha.1 - 2026-04-13

- chore: bump CommandTree dependency from 0.3.3 to 0.4.0

## 0.7.0-alpha.2 - 2026-04-11

- refactor: type-driven design — add `CheckOutcome` DU with `isPassed`/`isFailed` helpers (replacing `Passed: bool` + `Detail: string`), remove unused `_fileName` parameter from `checkProject`, handle malformed XML gracefully instead of crashing

## 0.7.0-alpha.1

- Version bump only

## 0.6.0-alpha.1

- chore: update NuGet dependencies

## 0.5.0-alpha.4

- Version bump only

## 0.5.0-alpha.3

- Version bump only

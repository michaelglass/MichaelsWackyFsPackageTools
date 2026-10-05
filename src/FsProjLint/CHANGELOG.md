# Changelog

## Unreleased

## 0.10.0-alpha.22 - 2026-10-05

- feat!: **a `Directory.Build.props` that does not parse fails once, naming that file, and the projects under it are skipped.** It used to fail every project under it with the same "XML parse" message. Now it is one `FAIL XML parse (<path to Directory.Build.props>)`, and each project under it gets one `SKIP Project checks` whose reason names the broken file: without the props file, neither the project's properties nor whether it is packable can be known. The exit code is still 1. A project that does not parse itself still fails "XML parse" on its own.
  - `LintResult` gains `PropsChecks`, each broken `Directory.Build.props` with its failure.

## 0.10.0-alpha.21 - 2026-09-30

- feat: **packable projects outside `src/` are checked.** A packable `.fsproj` at the repository root (FSharpLintAnalyzerShim's layout), under `tools/` or anywhere else now gets the project-level checks, and counts towards the repo-level checks that apply only to packable repos (`docs/index.md exists`, RefStamp). Projects under `src/` are all checked, as before. **A repo with a packable project outside `src/` can see new checks run, and newly fail.** Packable is the rule FsSemanticTagger uses to find the packages to release: a `PackageId`, `IsPackable` not `false`, and not an `Exe` without `PackAsTool`, so test, benchmark and example projects are left out.
- feat: **every project-level check reads a property from the fsproj, else the nearest `Directory.Build.props`,** as MSBuild does. `Version`, `Description`, `Authors`, `PackageLicenseExpression`, `RepositoryUrl`, `RepositoryType`, `TreatWarningsAsErrors`, `GenerateDocumentationFile`, `IncludeSymbols` and `SymbolPackageFormat` set only in `Directory.Build.props` now count, and a `Microsoft.SourceLink.GitHub` reference there satisfies "Has Microsoft.SourceLink.GitHub". Before, those checks read only the fsproj and failed such a project. An empty value in the fsproj overrides the props file, as in MSBuild. A `Directory.Build.props` that does not parse now fails the projects under it with an "XML parse" failure naming the file. "Local packs are ref-stamped" also accepts a RefStamp reference in each packable project's nearest `Directory.Build.props` (e.g. `src/Directory.Build.props`), not only the root one.
- feat: **new check "RepositoryUrl matches origin remote"** for packable projects: `RepositoryUrl` (from the fsproj, else the nearest `Directory.Build.props`) must name the repository the `origin` remote names, and so must `PackageProjectUrl` when it is a github.com URL. ssh and https spellings, a `.git` suffix and a trailing `/` are equivalent; github.com owner and name compare case-insensitively, so `union-config` vs `UnionConfig` still fails. Works in jj repos without a colocated `.git`. **This can newly fail a repo whose RepositoryUrl names another repository** (nuget.org's Source repository link for such a package is broken). The check is skipped, not failed, when there is no `origin` remote or a URL is not a hosted repository URL.
- feat: failed and skipped checks print their reason, and project-level checks name their project. A new `Skipped` outcome is listed under `Skipped:`, is left out of the pass count, and does not change the exit code.

## 0.10.0-alpha.20 - 2026-09-30

- chore(deps): CommandTree 0.12.0 → 0.13.0.
- fix: **the project scan under `src/` skips nested checkouts, dot-directories and build output.** A jj workspace or git worktree inside `src/` (a directory with its own `.jj` or `.git` entry), any dot-directory, build output (`bin`, `obj`, `artifacts`, `output`) and `node_modules` are no longer linted as more projects. This is the same rule FsSemanticTagger and SyncDocs apply.

## 0.10.0-alpha.19 - 2026-09-29

- chore(deps): CommandTree 0.11.3 → 0.12.0.

## 0.10.0-alpha.18 - 2026-09-29

- chore(deps): CommandTree 0.8.1 -> 0.11.3. An unknown command that is a near-miss of a real one now ends with a suggestion, e.g. `Unknown command 'chek'. Did you mean 'fsprojlint check'?`; commands, flags and help text are otherwise unchanged.

## 0.10.0-alpha.17 - 2026-09-15

- Finish: update SourceLink to patched 10.0.303
- Finish: update SourceLink to fix CVE-2026-62900


## 0.10.0-alpha.16 - 2026-08-25

- chore(deps): CommandTree 0.8.0 -> 0.8.1


## 0.10.0-alpha.15 - 2026-08-17

- docs: cut thinking-out-loud comments across src and tests


## 0.10.0-alpha.14 - 2026-07-23

- chore(deps): update dev-tools and external dependencies


## 0.10.0-alpha.13 - 2026-07-23

- deps: migrate to CommandTree 0.8.0


## 0.10.0-alpha.12 - 2026-07-23

- feat: new repo-level check "Local packs are ref-stamped (RefStamp)" — packable repos must wire in the RefStamp MSBuild guard so a local `dotnet pack` derives its version from the jj/git source ref and cannot produce a release-shaped version. Satisfied by a `<PackageReference Include="RefStamp" PrivateAssets="all" />` in a root `Directory.Build.props`/`Directory.Build.targets` (one line, repo-wide), by the same reference in every packable fsproj, or by a direct `<Import>` of `RefStamp.targets` (the shape RefStamp's own monorepo uses). Repos with no packable projects are unaffected.

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

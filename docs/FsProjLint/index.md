# FsProjLint

Opinionated F# project and repo validator for OSS readiness. Checks fsproj metadata, SourceLink, LICENSE, and more.

> **Status: early alpha, and substantially AI-written.** Runs the author's own F# OSS repos daily, but behavior and checks shift between versions and rough edges are expected — your mileage may vary. Issues and PRs welcome.

## Installation

```bash
dotnet tool install -g FsProjLint
```

## Usage

Run from the root of your repository:

```bash
fsprojlint
```

FsProjLint checks every `.fsproj` under `src/`, and every packable `.fsproj` elsewhere in the repository (a project at the root, under `tools/`, …), alongside repo-level requirements. Test, benchmark and example projects outside `src/` are not packable (see below) and are not checked. The scan skips build output (`bin`, `obj`, `artifacts`, `output`), `node_modules`, dot-directories and nested checkouts (jj workspaces, git worktrees). Exit code 0 means no check failed; exit code 1 means at least one check failed.

## Checks

### Repo-level checks

| Check | Description |
|-------|-------------|
| LICENSE exists | `LICENSE` or `LICENSE.md` at repo root |
| README.md exists | `README.md` at repo root |
| .editorconfig exists | `.editorconfig` at repo root |
| docs/index.md exists | Only required when at least one project is packable |
| No gitignored files in git history | No file matching the repo's `.gitignore` was ever committed on the **current branch's** ancestry — currently tracked **or** history-only. Scope is the branch you're on, not every branch/remote: a leak that lives only on an unrelated experiment branch does not fail the gate here (you care about the history you'll publish from this branch). A gitignored file in this branch's history leaks into the published history (and any clone/SourceLink), so it needs an untrack (currently tracked) or a history rewrite (history-only). Works for both Git and Jujutsu (jj) repos; passes when the directory is not a repo. |
| Local packs are ref-stamped (RefStamp) | Only required when the repo has packable projects. The [RefStamp](../RefStamp/) MSBuild guard must be wired in, so a local `dotnet pack` derives its version from the jj/git source ref and cannot produce a release-shaped version. Satisfied by a `<PackageReference Include="RefStamp" PrivateAssets="all" />` in a root `Directory.Build.props`/`Directory.Build.targets` (one line, repo-wide), by the same reference in every packable fsproj, or by a direct `<Import>` of `RefStamp.targets`. |

### Project properties

Every project-level check reads a property as MSBuild sees it: the value in the `.fsproj`, else the value in the nearest `Directory.Build.props` at or above the project's directory, up to the repository root. MSBuild imports only that nearest file, so a property it lacks is unset even when a props file further up sets it. A value in the `.fsproj` overrides the props file, even an empty one. `PackageReference` checks look in both files. Conditions are not evaluated. A `Directory.Build.props` that does not parse fails once, as "XML parse" naming that file, and the project-level checks of each project under it are skipped with a reason naming it.

### Project-level checks (all checked projects)

| Check | Description |
|-------|-------------|
| TreatWarningsAsErrors is true | Ensures warnings don't slip through |

### Project-level checks (packable projects only)

A project is packable when it has a `PackageId`, `IsPackable` is not `false`, and it is not an example app (an `OutputType` of `Exe` without `PackAsTool` set to `true`; benchmarks and samples are such apps, dotnet tools are not). FsSemanticTagger finds the packages to release with the same rule. Packable projects get additional checks:

| Check | Description |
|-------|-------------|
| Version present | `<Version>` element exists and is non-empty |
| Description present | `<Description>` element exists and is non-empty |
| Authors present | `<Authors>` element exists and is non-empty |
| PackageLicenseExpression present | License SPDX expression set |
| RepositoryUrl present | Source repository URL set |
| RepositoryType present | Repository type (e.g., `git`) set |
| GenerateDocumentationFile is true | XML docs generated for IntelliSense |
| IncludeSymbols is true | Symbol packages included |
| SymbolPackageFormat is snupkg | Uses the portable PDB symbol format |
| Has Microsoft.SourceLink.GitHub | SourceLink package referenced (in the fsproj or the nearest `Directory.Build.props`) for debugger support |
| RepositoryUrl matches origin remote | `RepositoryUrl` names the repository the project lives in, as recorded by the `origin` remote. nuget.org's "Source repository" link comes from `RepositoryUrl`, so a wrong one (e.g. `github.com/owner/union-config` for the repo `github.com/owner/UnionConfig`) is a broken link. `git@host:owner/repo`, `ssh://`, `https://`, a `.git` suffix and a trailing `/` all name the same repository; on github.com owner and name compare case-insensitively, elsewhere exactly. Works in git checkouts and in jj repos with or without a colocated `.git`. **Skipped** (not failed) when there is no `origin` remote, the directory is not a repository, or either URL is not a hosted repository URL (a local path, an unexpanded `$(...)`). A missing `RepositoryUrl` is left to "RepositoryUrl present". |
| PackageProjectUrl matches origin remote | Same rule for `PackageProjectUrl`, checked only when it is a github.com URL (a docs site or other host is not checked). Paths below `owner/repo` (e.g. `/tree/main/docs`) are allowed. |

## Example Output

Failed and skipped checks print their reason; project-level checks name their project.

```
FAILED:
  FAIL IncludeSymbols is true (src/MyLib/MyLib.fsproj)
       IncludeSymbols not found
  FAIL RepositoryUrl matches origin remote (src/MyLib/MyLib.fsproj)
       RepositoryUrl 'https://github.com/owner/my-lib' (in src/MyLib/MyLib.fsproj) names a different repository than the origin remote, https://github.com/owner/MyLib, so the package's Source repository link on nuget.org points at the wrong repository. Fix: set <RepositoryUrl>https://github.com/owner/MyLib</RepositoryUrl> in src/MyLib/MyLib.fsproj.
Passed:
  PASS LICENSE exists
  PASS README.md exists
  PASS .editorconfig exists
  PASS TreatWarningsAsErrors is true (src/MyLib/MyLib.fsproj)
  PASS Version present (src/MyLib/MyLib.fsproj)
  PASS Description present (src/MyLib/MyLib.fsproj)

Result: 6/8 checks passed
```

A check that cannot run here is listed under `Skipped:` with its reason and left out of the pass count, e.g. `Result: 6/8 checks passed, 1 skipped`. Skipped checks do not change the exit code.

## License

MIT

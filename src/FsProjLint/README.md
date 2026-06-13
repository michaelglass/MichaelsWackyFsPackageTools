# FsProjLint

Opinionated F# project and repo validator for OSS readiness. Checks fsproj metadata, SourceLink, LICENSE, and more.

## Installation

```bash
dotnet tool install -g FsProjLint
```

## Usage

Run from the root of your repository:

```bash
fsprojlint
```

FsProjLint discovers all `.fsproj` files under `src/` and checks them alongside repo-level requirements. Exit code 0 means everything passed; exit code 1 means at least one check failed.

## Checks

### Repo-level checks

| Check | Description |
|-------|-------------|
| LICENSE exists | `LICENSE` or `LICENSE.md` at repo root |
| README.md exists | `README.md` at repo root |
| .editorconfig exists | `.editorconfig` at repo root |
| docs/index.md exists | Only required when at least one project is packable |
| No gitignored files in git history | No file matching the repo's `.gitignore` was ever committed on the **current branch's** ancestry — currently tracked **or** history-only. Scope is the branch you're on, not every branch/remote: a leak that lives only on an unrelated experiment branch does not fail the gate here (you care about the history you'll publish from this branch). A gitignored file in this branch's history leaks into the published history (and any clone/SourceLink), so it needs an untrack (currently tracked) or a history rewrite (history-only). Works for both jj-backed (resolves the current commit via `jj log @-`) and plain-git (`HEAD`) repos; passes when the directory is not a repo. |

### Project-level checks (all projects)

| Check | Description |
|-------|-------------|
| TreatWarningsAsErrors is true | Ensures warnings don't slip through |

### Project-level checks (packable projects only)

A project is considered packable if it has a `PackageId` and `IsPackable` is not `false`. Packable projects get additional checks:

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
| Has Microsoft.SourceLink.GitHub | SourceLink package referenced for debugger support |

## Example Output

```
FAILED:
  FAIL IncludeSymbols is true
  FAIL Has Microsoft.SourceLink.GitHub
Passed:
  PASS LICENSE exists
  PASS README.md exists
  PASS .editorconfig exists
  PASS TreatWarningsAsErrors is true
  PASS Version present
  PASS Description present

Result: 6/8 checks passed
```

## License

MIT

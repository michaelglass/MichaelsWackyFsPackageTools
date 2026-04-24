# FsSemanticTagger

Automatic semantic versioning for F# NuGet packages. FsSemanticTagger inspects your compiled DLL to detect API changes and determines the correct version bump according to [Semantic Versioning](https://semver.org/).

## How It Works

FsSemanticTagger extracts the public API surface from your compiled assembly using reflection metadata. It compares the current API against the previously released version and classifies changes:

| Change type | Example | Version bump |
|------------|---------|-------------|
| **Breaking** (removed/changed) | Deleted a public method | Major (or minor if < 1.0) |
| **Addition** (new public API) | Added a new public type | Minor (or patch if < 1.0) |
| **No API change** | Internal refactoring | Patch |

## Installation

```bash
dotnet tool install -g FsSemanticTagger
```

## Usage

### Generate a config file

For monorepos with multiple packages, generate a `semantic-tagger.json` config:

```bash
fssemantictagger init
```

This scans for packable `.fsproj` files and writes a config with sensible defaults. For single-package repos, the tag prefix is `"v"`. For multi-package repos, each package gets a prefix like `"mylib-v"`.

### Extract the public API from a DLL

```bash
fssemantictagger extract-api path/to/MyLib.dll
```

Outputs one signature per line:

```
type MyNamespace.MyClass
  MyClass::MyMethod(int, string): bool
  MyClass::MyProperty: string
  MyClass::.ctor(int)
```

### Compare two versions of a DLL

```bash
fssemantictagger check-api old/MyLib.dll new/MyLib.dll
```

Exit codes:
- **0** -- No API changes
- **1** -- Non-breaking additions only
- **2** -- Breaking changes detected

### Orchestrate a release

```bash
# Auto-detect changes and bump version accordingly
fssemantictagger release

# Start a pre-release cycle
fssemantictagger alpha
fssemantictagger beta
fssemantictagger rc

# Promote to stable
fssemantictagger stable
```

The `release` command:
1. Checks for a clean working copy (no uncommitted changes)
2. Builds in Release configuration
3. Compares API against the previous release tag
4. Validates each bumped package's `CHANGELOG.md` has a non-empty `## Unreleased` section
5. Updates the version in your `.fsproj` file(s)
6. Promotes the `## Unreleased` section to `## <version> - YYYY-MM-DD` and inserts a fresh empty `## Unreleased` above it
7. Creates a VCS tag (supports both Git and [Jujutsu](https://jj-vcs.github.io/jj/))

### Changelog promotion

For each package being bumped the tool promotes its `CHANGELOG.md`'s `## Unreleased` section to a versioned header of the form:

```
## <version> - YYYY-MM-DD
```

A fresh empty `## Unreleased` heading is inserted above it so the file is ready for the next cycle. Both `## Unreleased` and `## [Unreleased]` are recognized (case-insensitive); the re-inserted heading is always unbracketed. The changelog edit lands in the same "Bump versions: ..." commit as the fsproj version update.

**Changelog location:**
- **Single-package repos** (one packable fsproj): `CHANGELOG.md` at the repo root.
- **Multi-package repos**: `CHANGELOG.md` next to each package's fsproj (and next to each path in `fsProjsSharingSameTag`).

**Fail-fast:** if any package needing a bump is missing `CHANGELOG.md`, is missing the `## Unreleased` section, or the section is empty, the release aborts with exit code 1 before any files are modified.

### Flags

All release commands (`release`, `alpha`, `beta`, `rc`, `stable`) accept:

- `--dry-run` — preview version bumps without modifying files or creating tags. Skips the clean-working-copy and CI checks; still builds and compares APIs so the preview is accurate. Missing or empty `## Unreleased` sections are reported as warnings instead of aborting.
- `--publish` — build and pack locally (`dotnet pack -c Release -o artifacts/`) instead of pushing tags for CI to publish.

```bash
# Preview what would be released
fssemantictagger release --dry-run

# Local build-and-pack instead of CI release
fssemantictagger release --publish
```

## Configuration

FsSemanticTagger works with zero configuration for simple projects. It auto-discovers a single packable `.fsproj` file (one with a `<PackageId>` element).

For monorepos or custom setups, create a `semantic-tagger.json`:

```json
{
  "packages": [
    {
      "name": "MyLib",
      "fsproj": "src/MyLib/MyLib.fsproj",
      "tagPrefix": "v",
      "fsProjsSharingSameTag": ["src/MyLib.Extensions/MyLib.Extensions.fsproj"]
    }
  ],
  "reservedVersions": ["1.0.0"]
}
```

### Config fields

| Field | Type | Description |
|-------|------|-------------|
| `packages` | array | List of packages to manage |
| `packages[].name` | string | Package name |
| `packages[].fsproj` | string | Path to the project file |
| `packages[].dllPath` | string? | Path to compiled DLL (auto-derived if omitted) |
| `packages[].tagPrefix` | string? | Git/jj tag prefix (default: `"v"`) |
| `packages[].fsProjsSharingSameTag` | string[]? | Other `.fsproj` files to update with the same version |
| `reservedVersions` | string[]? | Versions to skip |

## Pre-release Version Flow

FsSemanticTagger follows a structured pre-release progression:

```
0.1.0-alpha.1  ->  0.1.0-alpha.2  ->  0.1.0-beta.1  ->  0.1.0-rc.1  ->  0.1.0
```

- `alpha` / `beta` increments: bumps the pre-release number (e.g., `alpha.1` to `alpha.2`)
- If API changes are detected during `rc`, it drops back to `beta`
- `stable` removes the pre-release suffix

## VCS Support

FsSemanticTagger works with both Git and [Jujutsu](https://jj-vcs.github.io/jj/). It tries Jujutsu first, then falls back to Git. Tags and clean-working-copy checks work with either VCS.

## License

MIT

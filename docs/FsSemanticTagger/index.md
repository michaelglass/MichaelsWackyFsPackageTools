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
fssemantictagger release auto

# Start a pre-release cycle
fssemantictagger release alpha
fssemantictagger release beta
fssemantictagger release rc

# Promote to stable
fssemantictagger release stable
```

The `release` command:
1. Checks for a clean working copy (no uncommitted changes)
2. Builds in Release configuration
3. Compares API against the previous release tag
4. Updates the version in your `.fsproj` file(s)
5. Creates a VCS tag (supports both Git and [Jujutsu](https://jj-vcs.github.io/jj/))

Add `--publish` to build and pack locally instead of pushing tags for CI:

```bash
fssemantictagger release auto --publish
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
      "extraFsprojs": ["src/MyLib.Extensions/MyLib.Extensions.fsproj"]
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
| `packages[].extraFsprojs` | string[]? | Other `.fsproj` files to update with the same version |
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

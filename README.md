# MichaelsWackyFsPackageTools

<!-- sync:intro:start -->
A collection of dotnet CLI tools (and one MSBuild package) that aim to make maintaining F# open-source projects a little less tedious. I build them for my own repos; they might suit yours too.

| Tool | What it does |
|------|-------------|
| [FsSemanticTagger](src/FsSemanticTagger/) | Detects API changes in your compiled DLL and determines the correct semantic version bump |
| [SyncDocs](src/SyncDocs/) | Helps keep sections of your README in sync with your docs site |
| [FsProjLint](src/FsProjLint/) | Validates repo and project structure for NuGet-publishable F# projects (fsproj metadata, SourceLink, LICENSE, and more) |
| [RefStamp](src/RefStamp/) | MSBuild guard that derives local `dotnet pack` versions from the jj/git source ref -- a dev machine cannot produce a release-shaped version |

CoverageRatchet, formerly part of this collection, now lives at [michaelglass/CoverageRatchet](https://github.com/michaelglass/CoverageRatchet).
<!-- sync:intro:end -->

> **Status: early alpha, and substantially AI-written.** These tools run the
> author's own F# OSS repos daily, but behavior and APIs shift between versions
> and rough edges are expected — your mileage may vary. Issues and PRs welcome.

<!-- sync:getting-started:start -->
## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

### Install from NuGet

Each tool is a standalone dotnet tool. Install only the ones you need:

```bash
# Semantic versioning with API change detection
dotnet tool install -g FsSemanticTagger

# README-to-docs syncing
dotnet tool install -g SyncDocs

# Repo/project structure validation for NuGet-publishable F# projects
dotnet tool install -g FsProjLint
```

### Quick verification

After installing, verify each tool works:

```bash
fssemantictagger --help
syncdocs --help
fsprojlint --help
```
<!-- sync:getting-started:end -->

<!-- sync:tool-overviews:start -->
## Tool Overviews

### FsSemanticTagger

FsSemanticTagger inspects your compiled F# assembly to detect API changes and determines the correct version bump according to [Semantic Versioning](https://semver.org/):

- **Removed** a public type or method? That's a **breaking change** (major bump).
- **Added** a new public member? That's a **minor bump**.
- **No API changes?** That's a **patch bump**.

```bash
# Compare two versions of your DLL
fssemantictagger check-api old/MyLib.dll new/MyLib.dll

# Bump the version and tag based on the API diff since the last release
fssemantictagger release
```

See the [FsSemanticTagger README](src/FsSemanticTagger/) for release workflows and configuration.

### SyncDocs

SyncDocs helps keep documentation in sync between your README files and a `docs/` folder. Mark sections in your README with `<!-- sync:NAME:start -->` and `<!-- sync:NAME:end -->` markers, and SyncDocs copies those sections to matching targets in `docs/`.

```bash
# Check if docs are in sync (use in CI)
syncdocs check

# Update docs from READMEs
syncdocs sync
```

See the [SyncDocs README](src/SyncDocs/) for the full marker format and conventions.

### FsProjLint

FsProjLint is an opinionated checker for F# projects meant to be published to NuGet. Run it from your repo root and it discovers every `.fsproj` under `src/` and checks each one — plus repo-level requirements — for OSS readiness: fsproj package metadata, SourceLink, a LICENSE, and more. Exit code 0 means everything passed; exit code 1 means at least one check failed.

```bash
# Validate repo and project structure (use in CI)
fsprojlint
```

See the [FsProjLint README](src/FsProjLint/) for the full list of checks.
<!-- sync:tool-overviews:end -->

## Development

### Building from source

```bash
git clone https://github.com/michaelglass/MichaelsWackyFsPackageTools.git
cd MichaelsWackyFsPackageTools
dotnet build
dotnet test
```

### Project structure

```
src/
  FsSemanticTagger/    # Semantic version automation
  SyncDocs/            # README-to-docs syncing
  FsProjLint/          # Repo/project structure validation
tests/
  FsSemanticTagger.Tests/
  SyncDocs.Tests/
  FsProjLint.Tests/
docs/                  # Generated documentation (synced from READMEs)
```

### Running checks locally

If you have [mise](https://mise.jdx.dev/) installed:

```bash
mise run build       # Build all projects
mise run test        # Run all tests with coverage
mise run check       # Format, lint, and docs checks
mise run ci          # Full CI pipeline locally
```

The local gate must run on **.NET SDK 10.0.4xx**. `global.json` pins 10.0.401 with
`rollForward: latestPatch`, and CI's build job uses the same band. Branch coverage
counts depend on the SDK feature band: the F# compiler in 10.0.3xx and in 10.0.4xx
emits different branch points for identical code. A floor measured on one band
therefore fails, or passes by accident, on the other. Run `mise run ci` from a plain
shell, where mise supplies the SDK. A dev shell that exports `DOTNET_ROOT` for a
different SDK (for example a nix shell with 10.0.302) takes precedence over mise,
and the pin then refuses it with "A compatible .NET SDK was not found". That refusal
is intended. When moving to a new band, bump `global.json` and CI's `dotnet-version`
together and re-measure the branch floors.

## License

MIT

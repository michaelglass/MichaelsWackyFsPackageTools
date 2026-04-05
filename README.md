# MichaelsWackyFsPackageTools

<!-- sync:intro:start -->
A collection of three dotnet CLI tools that help me maintain my F# open-source projects. They might be useful for yours too!

| Tool | What it does |
|------|-------------|
| [CoverageRatchet](src/CoverageRatchet/) | Enforces per-file code coverage thresholds that automatically ratchet upward -- coverage can improve but shouldn't regress |
| [FsSemanticTagger](src/FsSemanticTagger/) | Detects API changes in your compiled DLL and determines the correct semantic version bump |
| [SyncDocs](src/SyncDocs/) | Helps keep sections of your README in sync with your docs site |
<!-- sync:intro:end -->

<!-- sync:getting-started:start -->
## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

### Install from NuGet

Each tool is a standalone dotnet tool. Install only the ones you need:

```bash
# Per-file coverage enforcement
dotnet tool install -g CoverageRatchet

# Semantic versioning with API change detection
dotnet tool install -g FsSemanticTagger

# README-to-docs syncing
dotnet tool install -g SyncDocs
```

### Quick verification

After installing, verify each tool works:

```bash
coverageratchet --help
fssemantictagger --help
syncdocs --help
```
<!-- sync:getting-started:end -->

<!-- sync:tool-overviews:start -->
## Tool Overviews

### CoverageRatchet

CoverageRatchet reads Cobertura XML coverage reports and enforces per-file thresholds. The key idea: thresholds only go **up**. When your tests improve coverage on a file, the threshold automatically ratchets to the new level so it shouldn't drop back down.

```bash
# Ratchet thresholds upward (default command)
coverageratchet

# Check current coverage against thresholds (use in CI)
coverageratchet check

# Set thresholds to current coverage (makes check pass immediately)
coverageratchet loosen
```

Configuration lives in a `coverage-ratchet.json` file. See the [CoverageRatchet README](src/CoverageRatchet/) for the full configuration format.

### FsSemanticTagger

FsSemanticTagger inspects your compiled F# assembly to detect API changes and determines the correct version bump according to [Semantic Versioning](https://semver.org/):

- **Removed** a public type or method? That's a **breaking change** (major bump).
- **Added** a new public member? That's a **minor bump**.
- **No API changes?** That's a **patch bump**.

```bash
# Compare two versions of your DLL
fssemantictagger check-api old/MyLib.dll new/MyLib.dll

# Orchestrate a full release
fssemantictagger release auto
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
  CoverageRatchet/     # Coverage threshold enforcement
  FsSemanticTagger/    # Semantic version automation
  SyncDocs/            # README-to-docs syncing
tests/
  CoverageRatchet.Tests/
  FsSemanticTagger.Tests/
  SyncDocs.Tests/
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

## License

MIT

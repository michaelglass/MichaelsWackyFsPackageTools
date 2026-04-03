# FSharpOssTooling

Monorepo containing three dotnet tools for F# OSS project infrastructure:
- CoverageRatchet — per-file coverage enforcement with automatic threshold ratcheting
- SyncDocs — README-to-docs section syncing
- FsSemanticTagger — semantic versioning with API change detection

## Build & Test

```bash
dotnet build
dotnet test
```

## Mise Tasks

```bash
mise run build
mise run test
mise run check
mise run ci
mise run format
mise run lint
mise run docs
mise run pack
```

## Conventions

- F# code formatted with Fantomas, linted with FSharpLint
- 4-space indentation
- VCS: jj (Jujutsu), not git
- Tests use xUnit v3 with Microsoft Testing Platform v2

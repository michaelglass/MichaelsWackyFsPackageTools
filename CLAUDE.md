# FSharpOssTooling

Monorepo containing four dotnet tools for F# OSS project infrastructure:
- CoverageRatchet — per-file coverage enforcement with automatic threshold ratcheting
- SyncDocs — README-to-docs section syncing
- FsSemanticTagger — semantic versioning with API change detection
- FsProjLint — validates repo/project structure for NuGet-publishable F# projects

## Tool Distribution

All tools are NuGet dotnet tools (`PackAsTool=true`), published via Trusted Publishing (OIDC) from `.github/workflows/release.yml`. Currently pre-release (alpha).

## Reusable GitHub Workflows

This repo exports reusable workflows consumed by other F# OSS repos:
- `michaels-wacky-build.yml` — CI pipeline (format, lint, build, test, coverage ratchet, docs)
- `michaels-wacky-release.yml` — tag-triggered release to NuGet + GitHub Release
- `michaels-wacky-docs.yml` — fsdocs build + GitHub Pages deploy

## Repo Structure

- `src/<Tool>/` — tool source code and per-tool README
- `.config/dotnet-tools.json` — local tool manifest (fantomas, fsharplint, fsdocs, own tools)
- `.editorconfig` — shared editor config (4-space F#, 2-space XML/JSON/YAML, LF, UTF-8)
- `fsharplint.json` — FSharpLint config (120 char lines, naming conventions)
- `mise.toml` — all mise tasks including `lint-project` (FsProjLint) and `coverage-check`

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

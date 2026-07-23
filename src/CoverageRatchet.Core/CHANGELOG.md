# Changelog — CoverageRatchet.Core

## Unreleased

## 0.1.0-alpha.4 - 2026-07-23

- docs: SyncDocs changelog entry for code-sourced blocks; audit per-tool READMEs


## 0.1.0-alpha.3 - 2026-06-03

- fix: `mergeFromCi` (used by `loosen-from-ci`) now only **lowers** a per-file floor toward the CI-measured value (`min`), never raises it. Previously it overwrote the floor with the CI value unconditionally, so a transiently-higher CI measurement would raise a floor above what CI stably hits — anti-converging, guaranteeing the next CI run trips its own floor. Each metric (line/branch) is minned independently and platform sections stay isolated.

## 0.1.0-alpha.2 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300
- feat: initial release — Cobertura XML parsing, per-file threshold checking, ratcheting, loosening, multi-platform config, and XML-level merge as an embeddable library (no CLI dependency)

# Changelog

## Unreleased

- feat: source a fenced code block from a region of a real `.fs`/`.fsx` file via a `src=` attribute on the start marker (`sync:NAME:start src=PATH`), delimited in the file by `// sync:NAME:start` / `// sync:NAME:end` comment markers — the region defaults to the block name (override with `#region`), common leading indentation is normalized, and the block is wrapped in an `fsharp` fence. Integrated into `sync` and `check` (refreshed before README -> docs propagation) so drift, a missing file, or a missing/duplicated/unterminated region breaks `check`
- feat: code-sourced blocks now work in **standalone** markdown docs, not just README pair-sources. Any `.md` under the repo root carrying a `src=` block (e.g. `docs/writing-plugins.md`) is refreshed/verified in place; build/VCS/vendor directories are skipped and a path that is already a pair source is never processed twice

## 0.13.0-alpha.2 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300

## 0.13.0-alpha.1 - 2026-04-27

- feat: real `--help` / `-h` / `help` support — explains the `README.md` ↔ `docs/index.md` discovery convention and the `<!-- sync:NAME:start/end -->` marker syntax (was previously just a one-line "Usage: ..." on bad input)

## 0.12.0-alpha.3 - 2026-04-22

- docs: attribute CHANGELOG entries to released versions

## 0.12.0-alpha.2 - 2026-04-15

- Version bump only

## 0.12.0-alpha.1 - 2026-04-13

- Version bump only

## 0.11.0-alpha.1 - 2026-04-13

- Version bump only

## 0.10.0-alpha.2 - 2026-04-11

- refactor: type-driven design — add `SyncMode` DU (replacing `check: bool`), `SyncOutcome`/`SyncError` split (fixing silent exit 0 on missing files), `SyncPair` record, `DiscoveryWarning` DU, `DiscoveryResult` record

## 0.10.0-alpha.1

- Version bump only

## 0.9.0-alpha.1

- chore: update NuGet dependencies

## 0.8.0-alpha.4

- Version bump only

## 0.8.0-alpha.3

- Version bump only

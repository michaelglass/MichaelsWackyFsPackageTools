# Changelog

## Unreleased

## 0.10.0-alpha.1 - 2026-04-27

- feat: top-level `--help` now lists every repo-level and project-level check fsprojlint runs, so users know what's being validated without reading the source
- feat: accept `-h` and `help` as aliases for `--help`

## 0.9.0-alpha.3 - 2026-04-22

- docs: attribute CHANGELOG entries to released versions

## 0.9.0-alpha.2 - 2026-04-15

- Version bump only

## 0.9.0-alpha.1 - 2026-04-13

- Version bump only

## 0.8.0-alpha.1 - 2026-04-13

- chore: bump CommandTree dependency from 0.3.3 to 0.4.0

## 0.7.0-alpha.2 - 2026-04-11

- refactor: type-driven design — add `CheckOutcome` DU with `isPassed`/`isFailed` helpers (replacing `Passed: bool` + `Detail: string`), remove unused `_fileName` parameter from `checkProject`, handle malformed XML gracefully instead of crashing

## 0.7.0-alpha.1

- Version bump only

## 0.6.0-alpha.1

- chore: update NuGet dependencies

## 0.5.0-alpha.4

- Version bump only

## 0.5.0-alpha.3

- Version bump only

# AGENTS.md

Guidance for AI coding agents working in this repo. See `CLAUDE.md` for the same + Claude-specific notes; start there if both exist.

## Project

Monorepo of four F# dotnet tools — CoverageRatchet, SyncDocs, FsSemanticTagger, FsProjLint — plus reusable GitHub workflows consumed by 7 sibling F# OSS repos under `/Users/michaelglass/Developer/opensource/`.

## Before you claim done

Run `mise run check`. It's the single source of truth for "is this shippable" — it runs format, lint, test, coverage-ratchet, and docs checks. CI runs the same thing. If it's green locally it's green in CI.

Don't skip format. Fantomas is strict; a missing blank line or wrong indent fails CI with exit code 99.

## VCS is jj, not git

- Don't create feature branches. Work on `@` (the working copy), advance `main` when done.
- Commit: `jj describe -m "message"` (already-staged files in the working copy).
- Push: `jj bookmark set main -r @ && jj git push`.
- Sibling repos are also jj-backed. For PRs there: `jj bookmark create <branch>`, `jj git push --bookmark <branch>`, then `gh pr create`.
- Never use `jj squash --into @-` if the parent is a remote-tracked commit — it's immutable. Make a new commit instead.

## Coverage workflow (easy to get wrong)

CoverageRatchet enforces per-file, per-platform coverage thresholds. Files with no entry default to **100% / 100%**, not a weaker fallback.

The split of responsibilities:

- `mise run coverage-ratchet` — **tightens** existing thresholds after coverage improves. Won't add new platform entries.
- `mise run coverage-loosen` — **adds** missing entries for the current platform from actual coverage. Use when `check` fails locally after `loosen-from-ci` wrote a CI-platform-only entry.
- `coverageratchet loosen-from-ci` — release-time only, integrates CI platform thresholds from a workflow artifact.

Common trap: after a release, `loosen-from-ci` writes a linux-only entry for some file. Locally on macOS, `mise run check` then fails because the file falls back to 100% on macOS. Fix: `mise run coverage-loosen`, then `mise run coverage-ratchet` to settle. Don't hand-edit the JSON.

## Tests

- Tests live in `tests/<Tool>.Tests/`.
- xUnit v3 + Microsoft Testing Platform v2, Unquote for assertions (`test <@ ... @>`).
- Reuse helpers from `tests/Tests.Common/TestHelpers.fs` — `withTempDir`, `withCapturedConsole`, `createTempDir`, `cleanupDir`.
- Prefer real code over mocks. File I/O in a temp dir is fine and common.
- Run a single project: `dotnet test --project tests/<Tool>.Tests/<Tool>.Tests.fsproj`.

## Releasing

Use the FsSemanticTagger CLI on this repo itself:

```
fssemantictagger release            # auto-detect bump level from API diff
fssemantictagger release --dry-run  # preview without mutating
fssemantictagger alpha              # force start of a new alpha cycle
fssemantictagger release --publish  # local pack instead of pushing tags
```

`release` orchestrates: build, CI check (via `loosen-from-ci` if CoverageRatchet is installed), CHANGELOG validation, fsproj version bump, CHANGELOG promotion, commit, tag, push.

CHANGELOGs require a non-empty `## Unreleased` section before release. Fail-fast: missing/empty Unreleased aborts before any writes.

## Sibling repos

When adding a feature or breaking change that sibling repos consume:

1. Ship it in this monorepo first (new version).
2. Wait for NuGet indexing (check `https://api.nuget.org/v3-flatcontainer/<package>/index.json` — usually <5 min for alpha).
3. Open PRs in sibling repos to bump their `.config/dotnet-tools.json` (and `mise.toml` if the CLI changed). Sibling list is in `.claude/projects/.../memory/project_oss_repos.md` or just check `ls /Users/michaelglass/Developer/opensource/`.

## Don'ts

- Don't push to main (or merge PRs) without explicit user confirmation.
- Don't create documentation files (`.md`) unless asked.
- Don't run destructive commands (`rm -rf`, `jj abandon @-`, force-push) without asking.
- Don't skip hooks (`--no-verify`) to make CI pass. Fix the underlying issue.
- Don't claim a task is done without running `mise run check` green.

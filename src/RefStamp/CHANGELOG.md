# Changelog

## Unreleased

## 0.1.0-alpha.3 - 2026-09-15

- Stamp project-reference dependency versions during local packing so they match the dependency packages produced from the same source. Release dependency versions remain unchanged.
- Fix local packs from empty jj merge commits by including every parent ref under one valid version prefix.

## 0.1.0-alpha.2 - 2026-07-23

- chore(deps): update dev-tools and external dependencies


## 0.1.0-alpha.1 - 2026-07-23

- feat: initial release. MSBuild guard (`build/RefStamp.props` + `build/RefStamp.targets`) that derives local `dotnet pack` versions from the jj/git source ref: `<Version>-ref.<change-id>.g<commit-id>[.dirty]` (jj) / `<Version>-ref.g<head-sha>[.dirty[.g<stash-sha>]]` (git). The version tracks the tree, so pack → edit → repack always yields a new version and NuGet's never-re-extract cache trap dies. Release builds (CI env or explicit `-p:ReleaseBuild=true`) keep the clean version; an indeterminable ref FAILS the pack — no silent fallback. The ref is stamped into the assembly's informational version too, so CLIs report the ref they were built from.

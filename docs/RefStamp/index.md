# RefStamp

MSBuild guard that derives local `dotnet pack` versions from the jj/git source ref — on a dev machine, choosing a release-shaped version label is not an available action.

> **Status: early alpha, and substantially AI-written.** Runs the author's own F# OSS repos daily, but behavior shifts between versions and rough edges are expected — your mileage may vary. Issues and PRs welcome.

## The problem

NuGet never re-extracts a package version it has already cached: pack `1.2.3-alpha.4`, fix a bug, pack `1.2.3-alpha.4` again, and every consumer keeps running the STALE bits — the version label lies about its contents, and the only way to find out is a `strings`-probe of a cached DLL. The fix is structural: make a local pack **incapable** of producing a version a published release could produce.

## What it does

With RefStamp referenced, `dotnet pack` versions become:

| Situation | Version |
|-----------|---------|
| Local pack, jj repo, clean | `<Version>-ref.<change-id>.g<commit-id>` |
| Local pack, jj repo, undescribed working copy | `<Version>-ref.<change-id>.g<commit-id>.dirty` |
| Local pack, git repo, clean | `<Version>-ref.g<head-sha>` |
| Local pack, git repo, dirty | `<Version>-ref.g<head-sha>.dirty.g<stash-sha>` |
| Release (CI env, or explicit `-p:ReleaseBuild=true`) | `<Version>`, untouched |
| Ref not determinable (no jj/git, jj broken) | **the pack FAILS** — never a silent clean version |

The version tracks the **tree**, not just the change: jj snapshots your edits into a new commit id, so pack → edit → repack always yields a new version and the stale-cache trap dies. Two different trees cannot share a version string.

The ref is stamped into the package version **and** the assembly's informational version, so a CLI built from a local pack reports the ref it was built from (e.g. `fshw --version`).

A "release" is only: an explicit `-p:ReleaseBuild=true` (passed by the release pipeline and by `fssemantictagger release --publish`), or a CI environment (`GITHUB_ACTIONS`/`CI` env vars). Everything else is a local pack.

## Installation

One line in your repo's root `Directory.Build.props` covers every project:

```xml
<ItemGroup>
  <PackageReference Include="RefStamp" Version="0.1.0-alpha.1" PrivateAssets="all" />
</ItemGroup>
```

Nothing else to configure. `fsprojlint` enforces the guard's presence in packable repos.

## Notes

- In a jj repo, the ref comes from `jj` itself; a broken jj **fails** the pack rather than falling back to git `HEAD` (which is stale under jj).
- An empty working-copy commit (the post-`jj new` shape) stamps the **parent** ref — the described change you just finished.
- "Dirty" in jj terms means a non-empty, **undescribed** working copy; described work is a real commit and stamps clean.
- The probe runs only when packing (`dotnet pack`), never on plain `dotnet build` — no version churn, no incremental-build invalidation in the inner loop.
- The compiled assembly in this package is a placeholder; the payload is `build/RefStamp.props` + `build/RefStamp.targets`.

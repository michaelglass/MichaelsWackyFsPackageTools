# Changelog

## Unreleased

- fix: **resuming a release no longer crashes on the very tag it exists to re-point.**
  Resuming an in-progress release means moving a tag, not creating one: an orphan tag —
  one whose package never reached the feed — already exists by definition, so the resume
  path always re-points. Neither backend moves a tag unasked. `jj tag set` refused with
  `Error: Refusing to move tag`, and the `git tag -a` fallback beneath it would have
  refused with `tag already exists`. In a non-colocated jj checkout there is no root
  `.git` at all, so the fallback did not refuse — it aborted with `fatal: not a git
  repository`, turning a recoverable "won't move that tag" into a hard crash *mid-release*,
  with the version bump already committed and pushed and not one tag created. Observed
  live releasing FsHotWatch. `tag set` now passes `--allow-move`, and the git fallback
  passes `-f`.

## 0.14.0-alpha.4 - 2026-08-17

- feat: **a changelog merge that buries a `## Unreleased` callout now fails structurally.**
  A callout — a blockquote opening with a heading (`> ### ⚠️ Read this first`) or a GitHub
  alert marker (`> [!WARNING]`) — must be the first content of `## Unreleased`. A merge that
  keeps both sides of an `## Unreleased` conflict prepends the incoming entries *above* it, so
  the one block whose job is to be read first stops being read first: no conflict marker, no
  duplicate entry, no empty section, nothing that "resolved cleanly and gated green" could
  catch. `release` (before any writes) and `release --check` now report it, naming the callout,
  its line, and where to move it back to.

  Narrow on purpose: a plain `> quoted line` is not a callout and is ignored wherever it sits,
  blockquotes inside fenced code are sample text, and only `## Unreleased` is checked — verified
  against every `CHANGELOG.md` in four consuming repos (25 files, 636 sections) with zero flags,
  and against the five FsHotWatch revisions where the burial actually happened, all five flagged.
  The checked set is each package's changelog plus the repo-root `CHANGELOG.md` when one exists:
  in a multi-package repo the root file is the reader-facing aggregate the tool never promotes,
  and is exactly where a callout lives. There is no opt-out — a flag would be flipped by the
  person mid-merge, which is the person the rule exists for.

## 0.14.0-alpha.3 - 2026-08-17

- fix: **a release no longer claims success when a pushed tag triggered nothing.**
  `release` printed "Tags pushed. GitHub Actions will handle the release." and exited `0`
  as soon as the pushes returned — an assertion about something nobody had checked. A tag
  can land on the remote and create no workflow event at all, and then that line is the
  only trace of a release that built and published nothing. Observed live: seven tags
  pushed in one command produced **zero** runs while all seven were present on the
  remote; the same seven pushed one at a time produced seven. The release now asks GitHub
  whether a run exists for each tag, and exits non-zero naming the tags and how to
  re-trigger them when any is unaccounted for.

  A tag whose run cannot be *checked* — `gh` missing, unauthenticated, rate-limited, or
  unparseable output — counts as **unconfirmed**, not as fine. Reporting success because
  the check itself could not run would rebuild the same defect one level down.
- fix: **a transient push failure no longer aborts a release half-done.** Each tag push is
  retried before giving up. Observed live: a release crashed twice with
  `sign_and_send_pubkey: ... communication with agent failed`, and the identical push
  succeeded minutes later with nothing changed — leaving the version-bump commit pushed
  and no tags, which is exactly the state that tempts a manual batch push (see above). A
  push that never succeeds still fails loudly rather than being swallowed.
- refactor: removed `Vcs.pushTags`, a wrapper that called `pushTagsAndConfirm` and then
  `|> ignore`d the list of unconfirmed tags — re-arming, one call away, exactly the
  silent-missing-trigger failure the confirmation was added to catch. It had no callers.
- docs: `Api.isPublished` and `Api.isPublishedViaFlatContainer` no longer claim to serve
  the post-push availability poll. That poll consumes the three-valued `FeedPresence`
  directly, precisely so `FeedUnknown` behaves like "published" instead of like "absent";
  both two-valued views are now marked as such, with `checkFeedPresence` named as the
  one to reach for.

## 0.14.0-alpha.2 - 2026-08-05

- fix: an orphan tag no longer wedges a package's releases forever. A release was treated as finished the moment its tag existed — but a tag is a promise to publish, not the publication. When the tag was created and the publish never landed on NuGet (a CI failure after tagging, or a tag that was never pushed), the tag sat at HEAD, so no source change could ever appear "since" it: every subsequent run printed `Skipping <pkg>: no changes since <tag>` then `No packages to release`, and that version could never be published. Re-running never recovered. "Finished" now means the tag exists **and** its package is actually on the feed: when the newest tag's version is definitely absent from the feed and there are no source changes since it, the release is resumed and finished at **that same version** — in place, with the existing tag left exactly where it is (never deleted, never re-created) and no re-bump or changelog re-roll. A newest tag whose package *is* published still skips exactly as before.
- feat: package publication is now a **three-valued** question — `OnFeed` / `NotOnFeed` / `FeedUnknown` — decided in one place (`Api.checkFeedPresence`) and shared by both the post-push availability poll and orphan-tag detection. The old boolean collapsed "the feed says this version does not exist" together with "we could not reach the feed", which is precisely the distinction a fail-safe needs. Only a definite `NotOnFeed` may drive a re-publish: a 404 for the package id, or a 200 whose `versions` array does not list the version. Every way of failing to get an answer — timeout, DNS failure, 5xx, auth failure, or an unreadable response body — is `FeedUnknown` and behaves exactly like "published", so an outage can never cause a spurious re-release. The `dotnet restore` probe now acts purely as a monotone upgrade: it can raise a verdict to `OnFeed` (rescuing a package published only to a private feed) but can never lower one.
- fix: a **published** package whose DLL cannot be read is no longer at risk of being re-released. Deciding "is this version published?" by extracting its API conflates two unrelated things: the API extractor reports `AbsentOnFeed` both when a package is genuinely unpublished *and* when it is published but no assembly can be located inside the `.nupkg` — which is the case for any MSBuild-only or content-only package (`RefStamp`, released from this repo, ships only `build/`, and the extractor really does answer `AbsentOnFeed` for its published newest version). Orphan detection now asks the feed directly and never consults the API extractor, so package internals cannot influence a release decision.

## 0.14.0-alpha.1 - 2026-08-04

- fix: diff the CLI grammar for PackAsTool packages so a breaking tool change cannot ship as a patch


## 0.13.0-alpha.20 - 2026-07-23

- fix: isCommitPushed revset direction (was a false positive for any commit atop pushed main)
- chore(deps): update dev-tools and external dependencies


## 0.13.0-alpha.19 - 2026-07-23

- feat: auto-first-release registered packages under the default (Auto) command
- fix: first-release version read tolerates a missing/unreadable fsproj
- deps: migrate to CommandTree 0.8.0


## 0.13.0-alpha.18 - 2026-07-23

- feat: grammar-aware versioning for CommandTree consumers (AUTOMATION-194). The version bump now also diffs a package's **realized CLI grammar** — the parse contract of its command tree: command names, positional arguments (name-order, optionality, list-ness, value type) and flags (long/short names, value arity) — and folds the result into the bump, taking the stronger of the grammar and API-signature verdicts. This closes a blind spot in the assembly-signature diff: a `[<Cmd(Name = "diff-api")>]` command rename, a `[<CmdFlag(Name = ...)>]` flag rename, a removed command/flag, a flag arity change (e.g. nullary → value, or required → inline-only), or a required-argument addition all keep the DU's type signature byte-identical yet break every old CLI invocation — previously under-bumped as a patch, now correctly a **major** break. Additive-only grammar changes (a new command, a new flag, a new optional/list argument, a required→optional relaxation) bump minor. The grammar is recovered structurally from the built assembly under `MetadataLoadContext` (metadata-only, mirroring CommandTree's own `buildUnionTree`), so no consumer code runs; non-CommandTree packages and any unreadable/ambiguous case fall back to the API diff unchanged. `check-api` surfaces grammar breaks too.
- feat: `release` no longer needs a hand-written `## Unreleased` entry (AUTOMATION-197). When a changed package's `## Unreleased` section is empty or missing, the release now **derives** it from the commit descriptions since that package's last tag: each commit's summary line becomes a bullet, grouped by conventional-commit prefix (breaking `!` first, then `feat`, `fix`, the remaining types, then un-prefixed commits), with the tool's own `Bump versions:` commits dropped and duplicates removed. A hand-authored `## Unreleased` is **never** clobbered — derivation only fills an empty section. Multi-line jj descriptions contribute just their summary line so the changelog stays readable; commits are attributed per package by path (own source + bundled-dependency dirs). A new `release --check` mode (wired into `mise run ci`) validates, without building or tagging, that every changed package has an authored-or-derivable entry, so an unnotable change is caught at PR time instead of aborting the release.
- fix: analyzer packages are now API-diffed instead of being mis-reported as orphan tags (AUTOMATION-196). The previous-release assembly resolver only looked under `lib/<tfm>/` (libraries) and `tools/<tfm>/any/` (dotnet tools). An FSharp.Analyzers.SDK analyzer package (`IncludeBuildOutput=false`, `DevelopmentDependency=true`) ships its assembly under `analyzers/dotnet/fs/<id>.dll` with **no** `lib/` folder, so the resolver never found the DLL, `extractFromNuGetCache` returned `None`, and after a successful restore that yielded nothing readable the prior release was classified `AbsentOnFeed` — a false "orphan tag" that meant every analyzer package (e.g. all of CommandTree.Analyzers' published alphas) was silently never diffed. The resolver now also searches `analyzers/**` (covering the `dotnet/fs` and `dotnet/cs` conventions and any TFM nesting), so an analyzer package's API is extracted and compared like any library. Genuinely-absent packages still classify `AbsentOnFeed` (real orphans are not masked), and `lib/`/`tools/` resolution is unchanged.
- change: `release --publish` now packs with `-p:ReleaseBuild=true` (AUTOMATION-123). Local-publish is the release pipeline running on a dev machine — it owns the clean semver it just computed, and the explicit flag is what the RefStamp guard honors; without it, a RefStamp-guarded repo would refuse the release-shaped version.

## 0.13.0-alpha.17 - 2026-07-15

- fix: the API differ now identifies a type by its **assembly name + full name**, not its short name, so a public member whose parameter/return/property type keeps the same short name but **moves to a different assembly** is correctly detected as a **breaking change**. Previously such a move was invisible — the two types rendered identically (e.g. a `RouteStore` parameter moving from `TestPrune.Core` to a package's own `Falco.RouteStore` both printed as `RouteStore`) — so the differ saw no change and computed a **minor** bump for what is actually a **major** break (a consumer passing the old type no longer compiles). **This can change the computed bump level for consumers:** an assembly-move break that previously released as minor/patch will now correctly release as major. The identity uses the assembly *name* only, never its *version*, so a routine dependency version bump is not mistaken for a breaking change. The rendered signatures (shown by `extract-api` / `check-api` and in release diff output) are now assembly-qualified, e.g. `System.String [System.Private.CoreLib]`.
- chore(deps): bump `System.Reflection.MetadataLoadContext` 10.0.8 → 10.0.9.

## 0.13.0-alpha.16 - 2026-06-17

- chore: the "waiting for CI" output during `release` now reads unmistakably as an expected wait rather than a hang. The entry messages spell out what is happening and how long it takes — "Waiting for CI on the version-bump commit to pass before pushing the tag (expected, ~1-2 min)..." (and the analogous "...on the release commit...before releasing..." on the `--push` / precondition path) — and each interim poll line is now phrased as progress ("...still waiting for CI to start (this is expected — not a hang)", "...CI still running (N/M runs complete — expected, not a hang)") instead of a bare repeated "Waiting for CI...". No behavior change; the tool still polls the bump/release commit's CI before pushing the tag.

## 0.13.0-alpha.15 - 2026-06-16

- feat: `--push` flag for `release`/`alpha`/`beta`/`rc`/`stable`. When the release commit isn't on the remote yet, `--push` pushes it and waits for its CI before proceeding. Off by default (auto-pushing to a branch-protected / PR-gated `main` is unsafe to do implicitly); the default behaviour is to fail fast and tell you to push.
- fix: `release` now **fails fast** when the release commit isn't pushed, *before* the expensive build / coverage reconciliation, with an actionable message ("the release commit isn't on the remote … push it, or pass `--push`"). Previously the tool ran the full local CI / `coverageratchet loosen-from-ci` first and only then failed — and worse, mislabelled the never-pushed commit as "CI failed for non-coverage reasons". A missing CI run (commit not pushed) is now reported as a push precondition, kept strictly distinct from a CI run that genuinely *failed* (which still errors and names the failing run's URL). A commit that *is* pushed but whose CI is still running is waited on as before.
- fix: `release --only <pkg>` on a multi-package repo now resolves the selected package's CHANGELOG from its own project directory, not the repo root. Previously `--only` narrowed the in-memory package list *before* the "single-package repo (root CHANGELOG) vs. multi-package repo (per-project CHANGELOG)" decision was taken, so scoping a monorepo down to one package made the tool mis-detect it as a single-package repo and abort with "CHANGELOG.md not found" (or validate the wrong file). `--only` now affects only the set of packages released; the repo's structural package set — which also drives separately-released dependency-boundary detection — is left intact.
- chore: the uncommitted-changes error now nudges jj users to `describe` `@` ("Commit (or, in jj, describe `@`) the working copy before releasing").

- refactor: `Shell.CommandResult.Failure` now carries the process exit code (`Failure of string * exitCode: int`), unifying it with `CoverageRatchet.Shell`'s shape so the two tools' Shell modules no longer diverge. No behavioral change today — every current match site reads only the message — but the exit code is now available, so a process that exits 1 ("nothing to do") can be told apart from one that exits 128 (e.g. a git/jj auth failure) without a future breaking change. `run` populates it from `Process.ExitCode`.

## 0.13.0-alpha.13 - 2026-06-12

- fix: the uncommitted-changes check (which guards `release`) now uses `jj diff --summary` emptiness instead of matching the English `jj status` banner ("The working copy is clean" / "...has no changes"). The check is now locale-independent and won't break if jj reworks its status wording. `getCiStatus`'s clean-working-copy parent fallback uses the same check, so it benefits too.
- fix: `release` now pushes the version-bump commit (`jj git push`) **before** creating any tag, and the resume path re-pushes main (idempotently) before tagging. Previously tags were created at local `main` and only then was the commit pushed; if that push failed, an orphan local tag pointed at a commit that never reached the remote, and the resume logic — which keys off "no tag at the fsproj version" — treated the wedged release as already done and never recovered it. Pushing main first closes that partial-failure window: a failed push leaves no tag, so the next run resumes cleanly.
- fix: `discover`/`findPackableProjects` no longer counts executable example apps as release candidates. A project with `<OutputType>Exe</OutputType>` but no `<PackAsTool>true</PackAsTool>` is now treated as a runnable example (not a NuGet package) and excluded; real dotnet tools (`Exe` + `PackAsTool`) and libraries with a `<PackageId>` are still kept. Previously any non-test fsproj with a `<PackageId>` was packable, so an example exe showed up as a phantom candidate. **Behavior change for `discover` consumers:** repos that worked around this with a `semantic-tagger.json` are unaffected; repos with an exe example and no config will stop seeing the phantom candidate (the intended fix) — a single-package repo that previously reported "multiple packable fsprojs" may now resolve cleanly.
- deps: bump CommandTree 0.6.2 -> 0.6.3.

## 0.13.0-alpha.12 - 2026-06-11

- fix: the post-push NuGet availability poll now checks the nuget.org flat-container `index.json` first, falling back to the `dotnet restore` probe only when the flat container hasn't indexed the version yet. The flat container is the fastest-updating publish surface (it's where `restore` downloads the `.nupkg` from), so a just-pushed release shows there well before the registration index that a restore resolves against. Previously the poll only ran a restore, which repeatedly timed out ("Timed out waiting for `<Pkg>` `<ver>` on NuGet") while the package was already live on the CDN and downloadable — a misleading warning that forced manual verification. Private feeds are unaffected: the flat-container check can't see a private-only package, so the poll falls through to the restore probe, which still honours the repo `nuget.config`. The poll still never changes the exit code, and `--skip-nuget-wait` is unchanged.

## 0.13.0-alpha.11 - 2026-06-10

- fix: `release` Auto now auto-recovers from an orphan tag (tag exists but the package never reached NuGet). The previous-release fetch is classified — package absent on the feed (NU1101/NU1102) vs transient fetch error — and absent orphans are skipped with a warning, computing the bump against the most recent *published* prior. Transient/network errors still abort (never guess a bump). If every prior tag is orphaned, the release bumps conservatively (NoChange, like the rebundle path) since no consumer ever received those versions.
- fix: `release` now works from a jj secondary workspace — `resolveGitDir` follows the `.jj/repo` pointer file to the real git store, so the tag push and `gh` CI queries get a valid `GIT_DIR` outside the default checkout (was: exit 134 `fatal: not a git repository` after the bump commit had already been pushed).

## 0.13.0-alpha.10 - 2026-06-09

- fix: dependency-aware rebundle now fires only for bundled references (PackAsTool or non-published helpers), not separately-published NuGet dependencies. A library's transitive `<ProjectReference>` closure is now pruned at every reference that is itself a separately-released package (configured in `semantic-tagger.json`): such a reference is consumed as a NuGet `<dependency>` rather than physically bundled, so a change to its source no longer triggers a pointless byte-identical republish of the library. A `PackAsTool` package still bundles its entire closure and rebundles on any transitive change; a non-published helper project is still bundled (and recursed through). Previously every transitive reference triggered a rebundle, over-republishing libraries whenever a separately-published dependency's source changed.

## 0.13.0-alpha.9 - 2026-06-09

- feat: dependency-aware version bump. A package's change-detection now also considers its transitive `<ProjectReference>` closure (auto-derived from the fsproj — no config field). A bundling package (e.g. a `PackAsTool` CLI that physically ships its referenced DLLs) is now re-released ("rebundle" bump) when a bundled dependency's source changes since its last tag, even if the package's own source is unchanged. A rebundle is a `NoChange`-style bump that skips API extraction (a bundled tool/exe has no meaningful public API) and, when the package's own `## Unreleased` section is missing or empty, auto-inserts a `- chore: rebuild to bundle updated dependencies` changelog entry. Normal own-source bumps keep the strict changelog validation and API-diff behavior unchanged.

## 0.13.0-alpha.8 - 2026-06-03

- feat: `release`/`alpha`/`beta`/`rc`/`stable` now **resume a bumped-but-untagged release** on re-run. If a package's fsproj `<Version>` is ahead of its latest tag and no tag exists at that version (e.g. a prior run bumped the version and rolled the changelog but the tag push failed because CI flaked), the tool detects the in-progress release, verifies the commit's CI is green, and pushes the missing tag — instead of reporting "No packages to release". Decided off desired end-state (version vs tag), not work-remaining, so a mid-release failure self-heals on the next run.
- feat: add `--only <names>` to `release`/`alpha`/`beta`/`rc`/`stable` (and `--dry-run`) to scope a run to specific package(s) by name (comma-separated; names match the `name` field in `semantic-tagger.json`). When omitted, all packages are processed as before. Scoped runs only consider the selected packages for version computation and tagging; the rest are out of scope entirely. An unknown name aborts with a clear error listing the valid names instead of silently no-opping.
- chore: bump CommandTree 0.6.1 -> 0.6.2 (revision-stamping target fix; no behavior change).

## 0.13.0-alpha.7 - 2026-06-02

- feat: after pushing tags in the default PushTags mode, `release`/`alpha`/`beta`/`rc`/`stable` now poll NuGet until each newly-released package version is restorable (indexed) before exiting, so the command only returns once the release is actually live. The poll never changes the exit code — tags are already pushed, so a timeout prints a warning and still exits 0. Pass `--skip-nuget-wait` to exit immediately after pushing tags instead.
- feat: add a `--version` flag that prints the installed tool version.
- fix: invalid CLI arguments now print a readable error message instead of the raw parser output.
- chore: bump CommandTree 0.5.1 → 0.6.1.

## 0.13.0-alpha.6 - 2026-05-28

- chore: bump CommandTree 0.5.0 → 0.5.1.

## 0.13.0-alpha.5 - 2026-05-28

- fix: `release` no longer crashes when diffing against a previously-published package whose public API references external dependencies (e.g. `Falco`). The prior release's assembly is loaded from the NuGet cache lib dir, which has no co-located `.deps.json`, so transitive dependency assemblies were never added to the `MetadataLoadContext` resolver and reading any dependency-referencing type threw `FileNotFoundException`. The resolver now walks the package's `.nuspec` dependency graph to resolve those lib dirs, and assembly extraction degrades to "couldn't read the previous API" instead of crashing.

## 0.13.0-alpha.4 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300, System.Reflection.MetadataLoadContext 10.0.5 -> 10.0.8

## 0.13.0-alpha.3 - 2026-05-26

- fix: `release` no longer ships a breaking change as a patch when the previous release's package isn't in the local NuGet cache. It now downloads the prior package to read its API (honoring the repo's `nuget.config` via `--configfile`), and if the previous API still can't be obtained it aborts loudly instead of silently assuming "no change".

## 0.13.0-alpha.2 - 2026-05-04

- fix: `--publish` (LocalPublish) mode no longer creates jj tags that are never pushed — tags are now only created in PushTags mode, preventing "no changes since <unpushed-tag>" false-skips on subsequent runs

## 0.13.0-alpha.1 - 2026-04-27

- feat: subcommand `--help` now emits per-command details (e.g. `fssemantictagger release --help` explains what `release`/`alpha`/`beta`/`rc`/`stable` do and what `--dry-run` / `--publish` mean)
- feat: top-level `--help` documents the `semantic-tagger.json` schema and shows examples
- feat: accept `-h` and `help` as aliases for `--help`

## 0.12.0-alpha.5 - 2026-04-24

- feat: `--dry-run` flag on `release`/`alpha`/`beta`/`rc`/`stable` previews version bumps without modifying files, creating tags, or running the clean-working-copy and CI checks. Missing or empty `## Unreleased` sections report as warnings instead of aborting.
- **Breaking:** CLI flags are now named (`--publish`, `--dry-run`) rather than positional booleans. Callers passing `release true` must switch to `release --publish`.
- fix: print error message when `coverageratchet loosen-from-ci` fails instead of silently exiting with code 1
- fix: `Shell.run` now falls back to stdout when stderr is empty on failure, so diagnostic messages from tools that write to stdout are not lost

## 0.12.0-alpha.4 - 2026-04-22

- feat: promote `## Unreleased` section to `## <version> - YYYY-MM-DD` on release, inserting a fresh empty `## Unreleased` above it. Single-package repos use repo-root `CHANGELOG.md`; multi-package repos use `CHANGELOG.md` next to each fsproj (and each `fsProjsSharingSameTag`). Release aborts fail-fast (exit 1, no writes) if any required CHANGELOG.md is missing, has no `## Unreleased` section, or the section is empty.
- **Breaking:** `Config.ToolConfig` gains a `RootDir: string` field (populated by `Config.load`). Callers constructing the record directly must supply it.

## 0.12.0-alpha.3 - 2026-04-20

- fix: `hasChangesSinceTag` always returned true — `jj diff --stat` outputs summary text even for zero changes; use `--summary` instead (empty when no changes, compact file list otherwise)

## 0.12.0-alpha.2 - 2026-04-15

- feat: idempotent release — detect already-bumped fsproj versions on retry, skip commit/push, resume from CI polling
- feat: return exit code 1 on post-push CI failure/timeout (was incorrectly returning 0)
- feat: bump CI polling timeout from 10 to 15 minutes
- refactor: extract `waitForCiAndPushTags` and `packLocally` helpers
- refactor: add `BumpDecision` type (`NeedsBump`/`AlreadyBumped`) and `readFsprojVersion` function
- refactor: share compiled `versionElementRegex` between read/update functions
- fix: trivially-true test assertion in CI failure test

## 0.12.0-alpha.1

- refactor: type-driven design — add `RunStatus`/`RunConclusion` DUs (replacing raw strings in `CiRunInfo`), `ApiChange` uses non-empty list pattern `head * rest`, `Version.tryParse` returns `Result` instead of throwing, `Config.load`/`discover` return `Result` instead of throwing, `HasPreviousRelease` drops redundant `tag` field
- fix: `withJjGitDir` now uses `resolveGitDir` with absolute paths and `.git` pre-check (matching CoverageRatchet fix)
- chore: bump CommandTree dependency to 0.4.0

## 0.10.0-alpha.1

- fix: bump CommandTree to 0.3.5, restore ReleaseOptions record (record-typed args with defaults now supported)

## 0.9.0-alpha.1

- feat: integrate `loosen-from-ci` into release workflow — automatically loosens coverage thresholds from CI before version bumps
- fix: loosen FsSemanticTagger Release.fs thresholds with per-platform entries
- style: format ReleaseTests.fs with Fantomas
- chore: update NuGet dependencies

## 0.8.0-alpha.4

- feat: wait for CI on version bump commit before pushing tags
- fix: lower Release.fs line coverage threshold to 93%

## 0.8.0-alpha.3

- refactor: remove dead `tagLastCommit`, extract TFM list, simplify reserved version check
- fix: push tags individually after main to trigger GitHub Actions
- fix: lower Vcs.fs Linux branch coverage threshold

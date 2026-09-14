# Changelog — CoverageRatchet.Core

## Unreleased

- feat: `ReaderOptions` makes the Cobertura reader's three filters — included extensions, excluded file-name patterns, excluded path patterns — reachable from outside, and every entry point gains a `*With` twin that takes one: `extractRawLinesWith`, `parseXmlWith`, `parseXmlsWith`, `parseFilesWith`, `parseFileWith`. The parameterless forms delegate to their twin with `ReaderOptions.defaults`, which is byte-for-byte what was hard-coded before, so nothing changes for a caller that does not ask.
- feat: `ReaderOptions.withExtensions` widens the reader to other languages. `Cobertura.extractRawLines` filtered to `.fs` through a private array, which meant a Cobertura report from a C# or VB project returned zero files and there was no way to widen it — even though everything downstream (`RawLine`, `buildCoverage`, `Thresholds.judge`, `Ratchet.ratchet`) is already language-neutral. Renaming `.cs` to `.fs` *inside the XML*, changing nothing else, was enough to make a real C# report parse, which is what showed the filter was the only language-specific thing in the path.

## 0.1.0-alpha.7 - 2026-08-30

- Finish: AUTOMATION-127 fail when configured coverage floors are unmeasured


## 0.1.0-alpha.6 - 2026-08-17

- feat: `FileCoverage` now carries `LinesCovered` and `LinesTotal` alongside `LinePct`. The covered-line count was already computed while parsing Cobertura and then discarded; it is the numerator that stays stable when the JIT-dependent denominator drifts.
- feat: `CountFloor`, `CountResult` and `checkCounts` add per-file floors on the absolute count of covered lines/branches. `Config`/`RawConfig` gain `CountFloors`/`RawCountFloors`, parsed from a separate `countFloors` config section so counts and percentages are never positionally confusable.
- feat: `ratchetCountFloors` raises count floors monotonically and never enrols new files; `baselineCountFloors` records current counts and *can* lower a floor — the deliberate re-baseline after removing covered code. Both preserve a recorded `reason` and leave other platforms' entries untouched.
- refactor: the raw platform-aware merge used by ratchet/loosen is now shared by both floor kinds (`mergeRawSection`), so percentage and count handling cannot drift apart. New entries are written platform-less, stated once where the code does it rather than threaded through as a parameter every caller passed `None`.
- feat: `toRawConfig` widens a platform-resolved `Config` back to a `RawConfig`. It was written out twice inline (`saveConfig` and `ratchetWithStatus`) and had to be kept field-for-field in step; it is now one documented function that says plainly what resolving already discarded.
- docs: the README's `FileCoverage`, `Override`, `CountFloor` and `Config` definitions are now sourced directly from the compiled source via SyncDocs code regions, so they cannot drift again — the previously published definitions were missing `LinesCovered`/`LinesTotal` and `CountFloors` and would not have compiled. Adds a `Count floors` section covering `checkCounts`, `ratchetCountFloors` and `baselineCountFloors`, and states that `loosen`/`mergeFromCi` are percentage-only by design.

## 0.1.0-alpha.5 - 2026-07-23

- chore(deps): update dev-tools and external dependencies


## 0.1.0-alpha.4 - 2026-07-23

- docs: SyncDocs changelog entry for code-sourced blocks; audit per-tool READMEs


## 0.1.0-alpha.3 - 2026-06-03

- fix: `mergeFromCi` (used by `loosen-from-ci`) now only **lowers** a per-file floor toward the CI-measured value (`min`), never raises it. Previously it overwrote the floor with the CI value unconditionally, so a transiently-higher CI measurement would raise a floor above what CI stably hits — anti-converging, guaranteeing the next CI run trips its own floor. Each metric (line/branch) is minned independently and platform sections stay isolated.

## 0.1.0-alpha.2 - 2026-05-27

- deps: bump Microsoft.SourceLink.GitHub 10.0.201 -> 10.0.300
- feat: initial release — Cobertura XML parsing, per-file threshold checking, ratcheting, loosening, multi-platform config, and XML-level merge as an embeddable library (no CLI dependency)

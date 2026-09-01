# Changelog — CoverageRatchet.Core

## Unreleased

- feat: `ReaderOptions` makes the Cobertura reader's three filters — included extensions, excluded file-name patterns, excluded path patterns — reachable from outside, and every entry point gains a `*With` twin that takes one: `extractRawLinesWith`, `parseXmlWith`, `parseXmlsWith`, `parseFilesWith`, `parseFileWith`. The parameterless forms delegate to their twin with `ReaderOptions.defaults`, which is byte-for-byte what was hard-coded before, so nothing changes for a caller that does not ask.
- feat: `ReaderOptions.withExtensions` widens the reader to other languages. `Cobertura.extractRawLines` filtered to `.fs` through a private array, which meant a Cobertura report from a C# or VB project returned zero files and there was no way to widen it — even though everything downstream (`RawLine`, `buildCoverage`, `Thresholds.judge`, `Ratchet.ratchet`) is already language-neutral. Renaming `.cs` to `.fs` *inside the XML*, changing nothing else, was enough to make a real C# report parse, which is what showed the filter was the only language-specific thing in the path.

## 0.1.0-alpha.10 - 2026-09-27

- feat: `resolveConfigFor platform raw` resolves the floors a named platform enforces (what a CI runner on that platform checks), whichever machine reads the file; `resolveConfig` is now `resolveConfigFor Platform.current`.
- feat: `overrideEntriesToJson` renders one file's percentage-floor entries exactly as `saveRawConfig` writes them, without writing anything.

## 0.1.0-alpha.9 - 2026-09-24

- fix: `saveRawConfig` edits the document on disk instead of rebuilding it from the map. A key whose entries are unchanged keeps the JSON node that was read — bytes, property order, unknown properties — a changed key is replaced, a removed key is deleted, a new key is appended, and a trailing newline is preserved. Whitespace and string escaping still normalise to the writer's, so a file the tool wrote is a fixed point.
- fix: `mergeRawSection` (behind `loosenRaw`, `ratchetRaw` and `baselineCountFloorsRaw`) tags a NEW entry with `Platform.current` when the file already carries platform-tagged entries; a file with no entries still gets a platform-less one.

## 0.1.0-alpha.8 - 2026-09-15

- Fix: make the coverage-ratchet JSON writer encoding-stable
- Finish: update SourceLink to patched 10.0.303
- Finish: update SourceLink to fix CVE-2026-62900


## 0.1.0-alpha.7 - 2026-08-30

- Finish: fail when configured coverage floors are unmeasured


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

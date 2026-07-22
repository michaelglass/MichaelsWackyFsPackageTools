module FsSemanticTagger.Tests.ChangelogTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Tests.Common.TestHelpers
open FsSemanticTagger.Changelog
open FsSemanticTagger.Version

let private v (s: string) =
    match tryParse s with
    | Ok v -> v
    | Error msg -> failwithf "bad test version %s: %s" s msg

let private sampleDate = DateTime(2026, 4, 22)

[<Fact>]
let ``validate returns NoFile when file missing`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        test <@ validateUnreleased path = Error(NoFile path) @>)

[<Fact>]
let ``validate returns NoUnreleasedSection when no Unreleased header`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- thing\n")
        test <@ validateUnreleased path = Error(NoUnreleasedSection path) @>)

[<Fact>]
let ``validate returns EmptyUnreleasedSection when header has no entries before next heading`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n\n- thing\n")
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``validate returns EmptyUnreleasedSection when Unreleased is the last heading with no entries`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n")
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``validate returns Ok when Unreleased has entries`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- feat: something\n\n## 0.1.0 - 2026-01-01\n")
        test <@ validateUnreleased path = Ok() @>)

[<Fact>]
let ``validate recognizes bracketed [Unreleased] header`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## [Unreleased]\n\n- feat: something\n")
        test <@ validateUnreleased path = Ok() @>)

[<Fact>]
let ``validate is case-insensitive on Unreleased`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## unreleased\n\n- feat: something\n")
        test <@ validateUnreleased path = Ok() @>)

[<Fact>]
let ``promote rewrites Unreleased header to version + date`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")

        File.WriteAllText(
            path,
            "# Changelog\n\n## Unreleased\n\n- feat: new thing\n\n## 0.1.0 - 2026-01-01\n\n- old\n"
        )

        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.2.0-alpha.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: new thing" @>
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>
        test <@ updated.Contains "- old" @>)

[<Fact>]
let ``promote inserts fresh Unreleased header above the promoted section`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- feat: new thing\n\n## 0.1.0 - 2026-01-01\n")
        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        let lines = File.ReadAllLines path
        let unreleasedIdx = lines |> Array.findIndex isUnreleasedHeading

        let versionIdx =
            lines |> Array.findIndex (fun l -> l.Trim() = "## 0.2.0-alpha.1 - 2026-04-22")

        test <@ unreleasedIdx < versionIdx @>
        test <@ lines[unreleasedIdx].Trim() = "## Unreleased" @>)

[<Fact>]
let ``promote preserves content above Unreleased`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        let header = "# Changelog\n\nIntro paragraph.\n\n"
        File.WriteAllText(path, header + "## Unreleased\n\n- item\n")
        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        let updated = File.ReadAllText path
        test <@ updated.StartsWith "# Changelog" @>
        test <@ updated.Contains "Intro paragraph." @>)

[<Fact>]
let ``promote normalizes bracketed Unreleased to unbracketed on re-insert`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## [Unreleased]\n\n- item\n")
        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        let lines = File.ReadAllLines path
        let unreleasedIdx = lines |> Array.findIndex isUnreleasedHeading
        test <@ lines[unreleasedIdx].Trim() = "## Unreleased" @>)

[<Fact>]
let ``promote then validate returns EmptyUnreleasedSection (idempotency)`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- item\n")
        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``promote handles Unreleased as the first line of the file`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "## Unreleased\n\n- item\n")
        promoteUnreleased path (v "0.2.0-alpha.1") sampleDate
        let lines = File.ReadAllLines path
        test <@ lines[0].Trim() = "## Unreleased" @>
        test <@ lines |> Array.exists (fun l -> l.Trim() = "## 0.2.0-alpha.1 - 2026-04-22") @>)

[<Fact>]
let ``validate rejects bracketed non-Unreleased heading`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## [0.1.0]\n\n- thing\n")
        test <@ validateUnreleased path = Error(NoUnreleasedSection path) @>)

// --- promoteOrInsert ---

[<Fact>]
let ``promoteOrInsert behaves like promote when Unreleased has content`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- feat: real thing\n\n## 0.1.0 - 2026-01-01\n")
        promoteOrInsert path (v "0.2.0-alpha.1") sampleDate "- chore: rebundle"
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.2.0-alpha.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: real thing" @>
        // The default bullet is NOT used when there is real content.
        test <@ not (updated.Contains "- chore: rebundle") @>)

[<Fact>]
let ``promoteOrInsert inserts heading and default bullet when section missing`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- old\n")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- chore: rebundle" @>
        // Older content preserved.
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>
        test <@ updated.Contains "- old" @>)

[<Fact>]
let ``promoteOrInsert inserts when Unreleased present but empty`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n\n- old\n")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- chore: rebundle" @>
        // Exactly one Unreleased heading remains (no stray empty one left behind).
        let lines = File.ReadAllLines path
        test <@ lines |> Array.filter isUnreleasedHeading |> Array.length = 1 @>)

[<Fact>]
let ``promoteOrInsert after insert keeps a fresh empty Unreleased above the version`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## 0.1.0 - 2026-01-01\n")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        let lines = File.ReadAllLines path
        let unreleasedIdx = lines |> Array.findIndex isUnreleasedHeading

        let versionIdx =
            lines |> Array.findIndex (fun l -> l.Trim() = "## 0.1.1 - 2026-04-22")

        test <@ unreleasedIdx < versionIdx @>
        // The freshly-inserted Unreleased is empty -> validate reports it empty.
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``promoteOrInsert creates file with header when missing`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        test <@ File.Exists path @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- chore: rebundle" @>)

[<Fact>]
let ``promoteOrInsert inserts at top when no level-1 title present`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        // No `# ` title — the fresh section must be inserted at the very top.
        File.WriteAllText(path, "## 0.1.0 - 2026-01-01\n\n- old\n")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        let lines = File.ReadAllLines path

        let versionIdx =
            lines |> Array.findIndex (fun l -> l.Trim() = "## 0.1.1 - 2026-04-22")

        let oldIdx = lines |> Array.findIndex (fun l -> l.Trim() = "## 0.1.0 - 2026-01-01")
        // New section precedes the previously-top section.
        test <@ versionIdx < oldIdx @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "- chore: rebundle" @>
        test <@ updated.Contains "- old" @>)

[<Fact>]
let ``promoteOrInsert handles empty Unreleased with no trailing blank line`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        // `## Unreleased` immediately followed by another heading (no blank between).
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n## 0.1.0 - 2026-01-01\n\n- old\n")
        promoteOrInsert path (v "0.1.1") sampleDate "- chore: rebundle"
        let lines = File.ReadAllLines path
        // Exactly one Unreleased heading (the stale empty one was dropped) and the
        // previous version content is preserved.
        test <@ lines |> Array.filter isUnreleasedHeading |> Array.length = 1 @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- chore: rebundle" @>
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>
        test <@ updated.Contains "- old" @>)

// --- deriveUnreleasedBullets (AUTOMATION-197) ---

[<Fact>]
let ``deriveUnreleasedBullets groups by conventional prefix, drops bodies + bump-version commits, keeps un-prefixed``
    ()
    =
    // jj descriptions are long/multi-line: only the summary (first line) is used.
    // The tool's own "Bump versions: ..." commit is noise and must be dropped.
    // An un-prefixed commit is kept verbatim under "other" (never lost).
    let descriptions =
        [ "feat: add --check mode\n\nLong body that must be dropped for readability."
          "fix: handle empty Unreleased section\nsecond summary-body line dropped"
          "chore: bump CommandTree 0.6.2 -> 0.6.3"
          "Bump versions: FsSemanticTagger 0.13.0-alpha.18"
          "tidy up without a conventional prefix" ]

    let expected =
        [ "- feat: add --check mode"
          "- fix: handle empty Unreleased section"
          "- chore: bump CommandTree 0.6.2 -> 0.6.3"
          "- tidy up without a conventional prefix" ]

    test <@ deriveUnreleasedBullets descriptions = expected @>

[<Fact>]
let ``deriveUnreleasedBullets sorts breaking-marked commits first and keeps the bang`` () =
    let descriptions = [ "fix: small fix"; "feat!: remove the legacy flag" ]

    let expected = [ "- feat!: remove the legacy flag"; "- fix: small fix" ]

    test <@ deriveUnreleasedBullets descriptions = expected @>

[<Fact>]
let ``deriveUnreleasedBullets returns empty for no descriptions`` () =
    test <@ List.isEmpty (deriveUnreleasedBullets []) @>

[<Fact>]
let ``deriveUnreleasedBullets returns empty when every commit is bump-version noise or blank`` () =
    test <@ List.isEmpty (deriveUnreleasedBullets [ "Bump versions: FsSemanticTagger 0.1.0"; "   "; "\n\n" ]) @>

[<Fact>]
let ``deriveUnreleasedBullets clusters other recognised types in first-seen order and preserves scope`` () =
    // No feat/fix: the remaining recognised types (rank 3) cluster by type in the
    // order each type first appears — both chores together (input order), then docs.
    let descriptions =
        [ "chore(deps): bump A"; "docs: tweak readme"; "chore(ci): pin runner" ]

    let expected =
        [ "- chore(deps): bump A"; "- chore(ci): pin runner"; "- docs: tweak readme" ]

    test <@ deriveUnreleasedBullets descriptions = expected @>

[<Fact>]
let ``deriveUnreleasedBullets de-duplicates identical summaries across differing bodies`` () =
    let descriptions = [ "fix: same fix\n\nbody one"; "fix: same fix\n\nbody two" ]

    test <@ deriveUnreleasedBullets descriptions = [ "- fix: same fix" ] @>

[<Fact>]
let ``deriveUnreleasedBullets treats a colon-prefixed unknown type as an other bullet`` () =
    // "wip:" matches the conventional prefix SHAPE but "wip" isn't a recognised
    // type, so it's kept verbatim in the trailing "other" group (after feat).
    let descriptions = [ "wip: still cooking"; "feat: real feature" ]

    let expected = [ "- feat: real feature"; "- wip: still cooking" ]

    test <@ deriveUnreleasedBullets descriptions = expected @>

[<Fact>]
let ``deriveUnreleasedBullets skips leading blank lines to find the summary`` () =
    // The summary is the first NON-blank line, so leading blanks are skipped.
    test <@ deriveUnreleasedBullets [ "\n\nfeat: after leading blank" ] = [ "- feat: after leading blank" ] @>

// --- promoteOrDerive (AUTOMATION-197) ---

[<Fact>]
let ``promoteOrDerive never clobbers a hand-authored Unreleased entry`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")

        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- feat: hand-written note\n\n## 0.1.0 - 2026-01-01\n")

        let result =
            promoteOrDerive path (v "0.2.0") sampleDate [ "fix: derived thing that must be ignored" ]

        test <@ result = Ok() @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.2.0 - 2026-04-22" @>
        test <@ updated.Contains "- feat: hand-written note" @>
        // The derived bullet is NOT used when the author wrote the section.
        test <@ not (updated.Contains "derived thing") @>)

[<Fact>]
let ``promoteOrDerive fills an empty Unreleased from commit descriptions`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n\n- old\n")

        let result =
            promoteOrDerive path (v "0.1.1") sampleDate [ "feat: derived feature"; "fix: derived fix" ]

        test <@ result = Ok() @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: derived feature" @>
        test <@ updated.Contains "- fix: derived fix" @>
        // Older content preserved and a fresh empty Unreleased remains on top.
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``promoteOrDerive fills an empty Unreleased that is the last line of the file`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        // `## Unreleased` is the FINAL line — no trailing blank or content after it.
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n")
        let result = promoteOrDerive path (v "0.1.1") sampleDate [ "feat: first note" ]
        test <@ result = Ok() @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: first note" @>
        // Exactly one Unreleased heading remains (no stray empty one left behind).
        let lines = File.ReadAllLines path
        test <@ lines |> Array.filter isUnreleasedHeading |> Array.length = 1 @>)

[<Fact>]
let ``promoteOrDerive inserts a derived section when there is no Unreleased heading`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- old\n")
        let result = promoteOrDerive path (v "0.1.1") sampleDate [ "feat: brand new" ]
        test <@ result = Ok() @>
        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: brand new" @>
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>)

[<Fact>]
let ``promoteOrDerive returns EmptyUnreleasedSection and writes nothing when empty and nothing derivable`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        let before = "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n\n- old\n"
        File.WriteAllText(path, before)
        // Only a Bump-versions noise commit -> derives nothing.
        let result =
            promoteOrDerive path (v "0.1.1") sampleDate [ "Bump versions: X 1.0.0" ]

        test <@ result = Error(EmptyUnreleasedSection path) @>
        // No write happened.
        test <@ File.ReadAllText path = before @>)

[<Fact>]
let ``formatError - NoFile`` () =
    test <@ formatError (NoFile "x.md") = "x.md: CHANGELOG.md not found" @>

[<Fact>]
let ``formatError - NoUnreleasedSection`` () =
    test <@ formatError (NoUnreleasedSection "x.md") = "x.md: no '## Unreleased' section" @>

[<Fact>]
let ``formatError - EmptyUnreleasedSection`` () =
    test <@ formatError (EmptyUnreleasedSection "x.md") = "x.md: '## Unreleased' section is empty" @>

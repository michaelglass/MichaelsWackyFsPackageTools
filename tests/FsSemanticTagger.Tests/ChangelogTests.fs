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

// --- deriveUnreleasedBullets ---

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

// --- consumer-visible PackageReference changes ---------------------------
// defect 2: releasing SqlHydra.Query.Pgvector 0.1.0-alpha.5
// promoted only the authored `## Unreleased` block, so the published changelog
// omitted the one change a consumer could observe — a PackageReference bump of
// SqlHydra.Query 4.1.0-beta.2 -> 4.1.0-beta.3. A dependency version is a fact in
// the fsproj, so it is derived from the fsproj, never from commit prose.

let private fsprojWith (items: string) =
    sprintf "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>%s</ItemGroup></Project>" items

[<Fact>]
let ``packageReferences reads Include items with a Version attribute or child element`` () =
    let xml =
        fsprojWith
            "<PackageReference Include=\"SqlHydra.Query\" Version=\"4.1.0-beta.3\" /><PackageReference Include=\"Npgsql\"><Version>8.0.0</Version></PackageReference>"

    test <@ packageReferences xml = Some(Map [ "Npgsql", "8.0.0"; "SqlHydra.Query", "4.1.0-beta.3" ]) @>

[<Fact>]
let ``packageReferences ignores build-only references, Update items and versionless items`` () =
    let xml =
        fsprojWith (
            "<PackageReference Include=\"Microsoft.SourceLink.GitHub\" Version=\"10.0.301\" PrivateAssets=\"All\" />"
            + "<PackageReference Include=\"Fantomas\" Version=\"7.0.0\"><PrivateAssets>all</PrivateAssets></PackageReference>"
            + "<PackageReference Update=\"FSharp.Core\" Version=\"6.0.7\" />"
            + "<PackageReference Include=\"CentrallyManaged\" />"
            + "<PackageReference Include=\"Kept\" Version=\"1.0.0\" PrivateAssets=\"compile\" />"
        )

    test <@ packageReferences xml = Some(Map [ "Kept", "1.0.0" ]) @>

[<Fact>]
let ``packageReferences reads a namespaced legacy project and joins conditional versions`` () =
    let xml =
        "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>"
        + "<PackageReference Include=\"Dep\" Version=\"1.0.0\" Condition=\"'$(TargetFramework)' == 'net8.0'\" />"
        + "<PackageReference Include=\"Dep\" Version=\"2.0.0\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />"
        + "<PackageReference Include=\"Dep\" Version=\"2.0.0\" />"
        + "</ItemGroup></Project>"

    test <@ packageReferences xml = Some(Map [ "Dep", "1.0.0, 2.0.0" ]) @>

[<Fact>]
let ``packageReferences is None for text that is not a project file`` () =
    test <@ packageReferences "<Project><ItemGroup>" = None @>

[<Fact>]
let ``diffPackageReferences reports bumps, additions and removals, sorted by id`` () =
    let before =
        Map [ "Stays", "1.0.0"; "SqlHydra.Query", "4.1.0-beta.2"; "Gone", "2.0.0" ]

    let after =
        Map [ "Stays", "1.0.0"; "SqlHydra.Query", "4.1.0-beta.3"; "Arrived", "3.0.0" ]

    test
        <@
            diffPackageReferences before after = [ Added("Arrived", "3.0.0")
                                                   Removed("Gone", "2.0.0")
                                                   Bumped("SqlHydra.Query", "4.1.0-beta.2", "4.1.0-beta.3") ]
        @>

[<Fact>]
let ``diffPackageReferences is empty when nothing a consumer sees changed`` () =
    let refs = Map [ "Stays", "1.0.0" ]
    test <@ List.isEmpty (diffPackageReferences refs refs) @>

[<Fact>]
let ``dependencyBullet names the package and both versions`` () =
    test
        <@
            dependencyBullet (Bumped("SqlHydra.Query", "4.1.0-beta.2", "4.1.0-beta.3")) = "- build(deps): bump SqlHydra.Query from 4.1.0-beta.2 to 4.1.0-beta.3"
        @>

    test <@ dependencyBullet (Added("Npgsql", "8.0.0")) = "- build(deps): add Npgsql 8.0.0" @>
    test <@ dependencyBullet (Removed("Old", "1.0.0")) = "- build(deps): remove Old (was 1.0.0)" @>

// --- planPromotion / applyPromotion ---------------------------------------
// One plan answers "what will promotion write?" for both `--check` and release,
// so the check can only certify what promotion delivers.

let private sqlHydraBump = Bumped("SqlHydra.Query", "4.1.0-beta.2", "4.1.0-beta.3")

let private sqlHydraBullet =
    "- build(deps): bump SqlHydra.Query from 4.1.0-beta.2 to 4.1.0-beta.3"

[<Fact>]
let ``planPromotion promotes an authored section as written and never merges commit summaries into it`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- chore: package metadata\n\n## 0.1.0 - 2026-01-01\n")

        test
            <@
                planPromotion path [ "docs: trim comments"; "chore: tooling" ] [] = Ok
                    { Source = Authored
                      DependencyBullets = [] }
            @>)

[<Fact>]
let ``planPromotion adds a dependency bump the authored section does not mention`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- chore: package metadata\n\n## 0.1.0 - 2026-01-01\n")

        test
            <@
                planPromotion path [] [ sqlHydraBump ] = Ok
                    { Source = Authored
                      DependencyBullets = [ sqlHydraBullet ] }
            @>)

[<Fact>]
let ``planPromotion leaves out a dependency change the authored section already names with its new version`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")

        File.WriteAllText(
            path,
            "# Changelog\n\n## Unreleased\n\n- feat!: require sqlhydra.query 4.1.0-beta.3\n- fix: drop Old\n\n## 0.1.0 - 2026-01-01\n\n- mentions Npgsql 8.0.0 in history\n"
        )

        let plan =
            planPromotion path [] [ sqlHydraBump; Removed("Old", "1.0.0"); Added("Npgsql", "8.0.0") ]

        // Case-insensitive on the id; a mention in an already-released section
        // does not count.
        test
            <@
                plan = Ok
                    { Source = Authored
                      DependencyBullets = [ "- build(deps): add Npgsql 8.0.0" ] }
            @>)

[<Fact>]
let ``planPromotion still adds a bump when the section names the package but not the new version`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- docs: SqlHydra.Query usage example\n")

        test
            <@
                planPromotion path [] [ sqlHydraBump ] = Ok
                    { Source = Authored
                      DependencyBullets = [ sqlHydraBullet ] }
            @>)

[<Fact>]
let ``planPromotion derives an empty section from commit summaries plus unmentioned dependency changes`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n")

        let plan =
            planPromotion
                path
                [ "chore(deps): update deps (incl. SqlHydra.Query 4.1.0-beta.3)"; "fix: a bug" ]
                [ sqlHydraBump; Added("Npgsql", "8.0.0") ]

        test
            <@
                plan = Ok
                    { Source =
                        Derived
                            [ "- fix: a bug"
                              "- chore(deps): update deps (incl. SqlHydra.Query 4.1.0-beta.3)" ]
                      DependencyBullets = [ "- build(deps): add Npgsql 8.0.0" ] }
            @>)

[<Fact>]
let ``planPromotion derives from dependency changes alone when commits give nothing`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n")

        test
            <@
                planPromotion path [ "Bump versions: X 1.0.0" ] [ sqlHydraBump ] = Ok
                    { Source = Derived []
                      DependencyBullets = [ sqlHydraBullet ] }
            @>)

[<Fact>]
let ``planPromotion is EmptyUnreleasedSection when nothing is authored or derivable`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n")
        test <@ planPromotion path [ "Bump versions: X 1.0.0" ] [] = Error(EmptyUnreleasedSection path) @>)

[<Fact>]
let ``planPromotion is NoFile when the changelog is missing and nothing is derivable`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        test <@ planPromotion path [] [] = Error(NoFile path) @>)

// POSITIVE CONTROL: an all-authored release promotes byte-for-byte the same as
// a plain promoteUnreleased.
[<Fact>]
let ``applyPromotion of an authored plan with no dependency changes promotes unchanged`` () =
    withTempDir (fun dir ->
        let text =
            "# Changelog\n\n## Unreleased\n\n- chore: package metadata\n\n## 0.1.0 - 2026-01-01\n\n- old\n"

        let viaPlan = Path.Combine(dir, "plan.md")
        let viaPromote = Path.Combine(dir, "promote.md")
        File.WriteAllText(viaPlan, text)
        File.WriteAllText(viaPromote, text)

        applyPromotion
            viaPlan
            (v "0.2.0")
            sampleDate
            { Source = Authored
              DependencyBullets = [] }

        promoteUnreleased viaPromote (v "0.2.0") sampleDate
        test <@ File.ReadAllText viaPlan = File.ReadAllText viaPromote @>)

[<Fact>]
let ``applyPromotion appends dependency bullets after the authored entries, inside the new version section`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")

        File.WriteAllText(
            path,
            "# Changelog\n\n## Unreleased\n\n> ### Read this first\n\n- chore: package metadata\n\n\n## 0.1.0 - 2026-01-01\n\n- old\n"
        )

        applyPromotion
            path
            (v "0.2.0")
            sampleDate
            { Source = Authored
              DependencyBullets = [ sqlHydraBullet ] }

        let expected =
            [| "# Changelog"
               ""
               "## Unreleased"
               ""
               "## 0.2.0 - 2026-04-22"
               ""
               "> ### Read this first"
               ""
               "- chore: package metadata"
               sqlHydraBullet
               ""
               ""
               "## 0.1.0 - 2026-01-01"
               ""
               "- old" |]

        test <@ File.ReadAllLines path = expected @>)

[<Fact>]
let ``applyPromotion appends dependency bullets when the authored section is the end of the file`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n- chore: note")

        applyPromotion
            path
            (v "0.2.0")
            sampleDate
            { Source = Authored
              DependencyBullets = [ sqlHydraBullet ] }

        test
            <@
                File.ReadAllLines path = [| "# Changelog"
                                            ""
                                            "## Unreleased"
                                            ""
                                            "## 0.2.0 - 2026-04-22"
                                            ""
                                            "- chore: note"
                                            sqlHydraBullet |]
            @>)

[<Fact>]
let ``applyPromotion writes derived bullets followed by dependency bullets`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n\n- old\n")

        applyPromotion
            path
            (v "0.1.1")
            sampleDate
            { Source = Derived [ "- feat: derived feature" ]
              DependencyBullets = [ sqlHydraBullet ] }

        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.IndexOf "- feat: derived feature" < updated.IndexOf sqlHydraBullet @>
        test <@ updated.IndexOf sqlHydraBullet < updated.IndexOf "## 0.1.0 - 2026-01-01" @>
        // A fresh empty Unreleased remains on top, exactly once.
        test <@ validateUnreleased path = Error(EmptyUnreleasedSection path) @>
        test <@ File.ReadAllLines path |> Array.filter isUnreleasedHeading |> Array.length = 1 @>)

[<Fact>]
let ``applyPromotion inserts a derived section when there is no Unreleased heading`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        File.WriteAllText(path, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- old\n")

        applyPromotion
            path
            (v "0.1.1")
            sampleDate
            { Source = Derived [ "- feat: brand new" ]
              DependencyBullets = [] }

        let updated = File.ReadAllText path
        test <@ updated.Contains "## 0.1.1 - 2026-04-22" @>
        test <@ updated.Contains "- feat: brand new" @>
        test <@ updated.Contains "## 0.1.0 - 2026-01-01" @>)

// --- callout order -------------------------------------------------------
// A callout is a blockquote opening with a heading or a GitHub alert marker:
// the "read this first" banner. It exists to be read first, so it must be the
// first content of `## Unreleased`. A merge that prepends entries above it
// silently demotes it, which is exactly what these tests pin.

let private write (dir: string) (text: string) =
    let path = Path.Combine(dir, "CHANGELOG.md")
    File.WriteAllText(path, text)
    path

/// The shape of the real incident: five merges each prepended their entries
/// above the callout, which sank from the top of the section to below them.
let private buriedCallout =
    "# Changelog\n\n\
     ## Unreleased\n\n\
     - fix: entry that arrived in merge A\n\
     - feat: entry that arrived in merge B\n\n\
     > ### Read this first if you run fshw in CI or from a script\n\
     >\n\
     > fshw stop is not a remedy, and never was.\n\n\
     ## 0.1.0 - 2026-01-01\n\n- old\n"

/// The same document before the merge: the callout leads the section.
let private calloutFirst =
    "# Changelog\n\n\
     ## Unreleased\n\n\
     > ### Read this first if you run fshw in CI or from a script\n\
     >\n\
     > fshw stop is not a remedy, and never was.\n\n\
     - fix: entry that arrived in merge A\n\
     - feat: entry that arrived in merge B\n\n\
     ## 0.1.0 - 2026-01-01\n\n- old\n"

[<Fact>]
let ``callout order - entries prepended above the callout fail`` () =
    withTempDir (fun dir ->
        let path = write dir buriedCallout

        test
            <@
                validateCalloutOrder path = Error(
                    CalloutNotFirst(path, "Read this first if you run fshw in CI or from a script", 8)
                )
            @>)

// POSITIVE CONTROL: the rule must not be "reject every section with a blockquote".
[<Fact>]
let ``callout order - a callout that leads the section passes`` () =
    withTempDir (fun dir -> test <@ validateCalloutOrder (write dir calloutFirst) = Ok() @>)

[<Fact>]
let ``callout order - a GitHub alert callout is recognised`` () =
    withTempDir (fun dir ->
        let path =
            write dir "# Changelog\n\n## Unreleased\n\n- fix: thing\n\n> [!WARNING]\n> Breaking.\n"

        test <@ validateCalloutOrder path = Error(CalloutNotFirst(path, "[!WARNING]", 7)) @>)

[<Fact>]
let ``callout order - a leading GitHub alert callout passes`` () =
    withTempDir (fun dir ->
        let path =
            write dir "# Changelog\n\n## Unreleased\n\n> [!WARNING]\n> Breaking.\n\n- fix: thing\n"

        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - a plain blockquote is not a callout`` () =
    withTempDir (fun dir ->
        let path =
            write dir "# Changelog\n\n## Unreleased\n\n- fix: the daemon printed\n\n  > waiting on build\n"

        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - a callout inside a fenced code block is sample text`` () =
    withTempDir (fun dir ->
        let path =
            write
                dir
                "# Changelog\n\n\
                 ## Unreleased\n\n\
                 - docs: show how to write a callout\n\n\
                 ```markdown\n\
                 > ### Read this first\n\
                 ```\n"

        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - a heading inside the leading callout does not re-trigger`` () =
    withTempDir (fun dir ->
        let path =
            write
                dir
                "# Changelog\n\n\
                 ## Unreleased\n\n\
                 > ### Read this first\n\
                 >\n\
                 > #### Exit codes\n\
                 >\n\
                 > Four runs that were green can now be red.\n\n\
                 - fix: thing\n"

        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - a callout in a released section is not the Unreleased rule's business`` () =
    withTempDir (fun dir ->
        let path =
            write
                dir
                "# Changelog\n\n\
                 ## Unreleased\n\n\
                 - fix: thing\n\n\
                 ## 0.1.0 - 2026-01-01\n\n\
                 - old\n\n\
                 > ### Read this first\n"

        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - no Unreleased section is not a callout problem`` () =
    withTempDir (fun dir ->
        let path = write dir "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- old\n"
        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - an empty Unreleased section is not a callout problem`` () =
    withTempDir (fun dir ->
        let path = write dir "# Changelog\n\n## Unreleased\n\n## 0.1.0 - 2026-01-01\n"
        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - a missing file is validateUnreleased's error, not this one`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "CHANGELOG.md")
        test <@ validateCalloutOrder path = Ok() @>)

[<Fact>]
let ``callout order - the buried document still passes the emptiness check`` () =
    // The two rules are independent: the buried document has content, so
    // `validateUnreleased` is Ok and only the order rule catches it.
    withTempDir (fun dir -> test <@ validateUnreleased (write dir buriedCallout) = Ok() @>)

[<Fact>]
let ``formatError - NoFile`` () =
    test <@ formatError (NoFile "x.md") = "x.md: CHANGELOG.md not found" @>

[<Fact>]
let ``formatError - NoUnreleasedSection`` () =
    test <@ formatError (NoUnreleasedSection "x.md") = "x.md: no '## Unreleased' section" @>

[<Fact>]
let ``formatError - EmptyUnreleasedSection`` () =
    test <@ formatError (EmptyUnreleasedSection "x.md") = "x.md: '## Unreleased' section is empty" @>

[<Fact>]
let ``formatError - CalloutNotFirst names the callout, the line and the fix`` () =
    let text = formatError (CalloutNotFirst("x.md", "Read this first", 42))

    let expected =
        String.concat
            "\n"
            [ "x.md: the '## Unreleased' callout is buried — it is not the first thing in the section."
              "    Callout: \"Read this first\" (line 42)."
              "    A callout — a blockquote opening with a heading ('> ### ...') or an alert ('> [!WARNING]') —"
              "    exists to be read FIRST, so it must be the first content under '## Unreleased'."
              "    Fix: move the whole '> ...' block back to directly under the '## Unreleased' heading, above"
              "    every entry. The usual cause is a merge that prepended its entries above it."
              "    If this blockquote is not a callout, drop its leading heading or alert marker — a plain"
              "    '> quote' is ignored by this check." ]

    test <@ text = expected @>

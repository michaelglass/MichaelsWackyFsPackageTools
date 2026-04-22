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

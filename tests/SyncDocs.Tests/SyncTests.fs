module SyncDocs.Tests.SyncTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open SyncDocs.Sync
open Tests.Common.TestHelpers

// --- extractSections tests ---

[<Fact>]
let ``extractSections - finds multiple sections from README with sync tags`` () =
    let content =
        """# My Project
<!-- sync:intro:start -->
Hello world
<!-- sync:intro:end -->
Some middle text
<!-- sync:usage:start -->
Use it like this
<!-- sync:usage:end -->
"""

    let sections = extractSections content

    test <@ sections.Count = 2 @>
    test <@ sections.ContainsKey "intro" @>
    test <@ sections.ContainsKey "usage" @>
    test <@ sections["intro"].Contains "Hello world" @>
    test <@ sections["usage"].Contains "Use it like this" @>

[<Fact>]
let ``extractSections - supports hyphenated section names`` () =
    let content =
        """<!-- sync:getting-started:start -->
Quick start guide
<!-- sync:getting-started:end -->
"""

    let sections = extractSections content

    test <@ sections.Count = 1 @>
    test <@ sections.ContainsKey "getting-started" @>
    test <@ sections["getting-started"].Contains "Quick start guide" @>

[<Fact>]
let ``extractSections - returns empty map when no sections`` () =
    let content = "# Just a plain README\nNo sync tags here."

    let sections = extractSections content

    test <@ sections.IsEmpty @>

[<Fact>]
let ``extractSections - returns empty map for empty string`` () =
    let sections = extractSections ""
    test <@ sections.IsEmpty @>

[<Fact>]
let ``extractSections - single section`` () =
    let content =
        """<!-- sync:only:start -->
The only section
<!-- sync:only:end -->"""

    let sections = extractSections content
    test <@ sections.Count = 1 @>
    test <@ sections["only"].Contains "The only section" @>

[<Fact>]
let ``extractSections - ignores malformed tags`` () =
    let content =
        """<!-- sync:good:start -->
Good section
<!-- sync:good:end -->
<!-- sync:bad:start -->
Bad section missing end
<!-- sync:wrong:end -->
"""

    let sections = extractSections content
    test <@ sections.Count = 1 @>
    test <@ sections.ContainsKey "good" @>

// --- replaceSections tests ---

[<Fact>]
let ``replaceSections - replaces content between target sync tags`` () =
    let target =
        """# Docs
<!-- sync:intro -->
Old intro content
<!-- sync:intro:end -->
"""

    let sections = Map.ofList [ "intro", "\nNew intro content\n" ]
    let result = replaceSections target sections

    test <@ result.Contains "New intro content" @>
    test <@ not (result.Contains "Old intro content") @>

[<Fact>]
let ``replaceSections - preserves non-synced content in target`` () =
    let target =
        """# Docs
Some static content
<!-- sync:intro -->
Old intro
<!-- sync:intro:end -->
More static content
"""

    let sections = Map.ofList [ "intro", "\nNew intro\n" ]
    let result = replaceSections target sections

    test <@ result.Contains "Some static content" @>
    test <@ result.Contains "More static content" @>
    test <@ result.Contains "New intro" @>

[<Fact>]
let ``replaceSections - handles multiple sections in one target`` () =
    let target =
        """<!-- sync:intro -->
Old intro
<!-- sync:intro:end -->
Middle
<!-- sync:usage -->
Old usage
<!-- sync:usage:end -->
"""

    let sections = Map.ofList [ "intro", "\nNew intro\n"; "usage", "\nNew usage\n" ]

    let result = replaceSections target sections

    test <@ result.Contains "New intro" @>
    test <@ result.Contains "New usage" @>
    test <@ result.Contains "Middle" @>
    test <@ not (result.Contains "Old intro") @>
    test <@ not (result.Contains "Old usage") @>

[<Fact>]
let ``replaceSections - leaves target unchanged when section name not in map`` () =
    let target =
        """<!-- sync:intro -->
Old intro
<!-- sync:intro:end -->
"""

    let sections = Map.ofList [ "other", "\nNew content\n" ]
    let result = replaceSections target sections
    test <@ result = target @>

[<Fact>]
let ``replaceSections - handles dollar signs in replacement content`` () =
    let target =
        """<!-- sync:intro -->
Old content
<!-- sync:intro:end -->
"""

    let sections = Map.ofList [ "intro", "\nPrice is $100\n" ]
    let result = replaceSections target sections
    test <@ result.Contains "Price is $100" @>

[<Fact>]
let ``replaceSections - empty sections map returns content unchanged`` () =
    let target = "Some content with no replacements"
    let result = replaceSections target Map.empty
    test <@ result = target @>

[<Fact>]
let ``replaceSections - target has no matching tags returns unchanged`` () =
    let target = "# Docs\nJust plain text, no sync tags at all."
    let sections = Map.ofList [ "intro", "\nNew content\n" ]
    let result = replaceSections target sections
    test <@ result = target @>

// --- syncPair tests ---

[<Fact>]
let ``syncPair Check - returns InSync when section content matches`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(
            source,
            """<!-- sync:intro:start -->
Hello
<!-- sync:intro:end -->"""
        )

        File.WriteAllText(
            target,
            """<!-- sync:intro -->
Hello
<!-- sync:intro:end -->"""
        )

        let result = syncPair Check source target

        test <@ result = Ok InSync @>)

[<Fact>]
let ``syncPair Check - returns OutOfSync when section content differs`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(
            source,
            """<!-- sync:intro:start -->
New content
<!-- sync:intro:end -->"""
        )

        File.WriteAllText(
            target,
            """<!-- sync:intro -->
Old content
<!-- sync:intro:end -->"""
        )

        let result = syncPair Check source target

        test <@ result = Ok OutOfSync @>)

[<Fact>]
let ``syncPair - returns SourceMissing error when source does not exist`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "nonexistent.md")
        let target = Path.Combine(tmpDir, "index.md")
        File.WriteAllText(target, "some content")

        let result = syncPair Apply source target

        test <@ result = Error(SourceMissing source) @>)

[<Fact>]
let ``syncPair - returns TargetMissing error when target does not exist`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "nonexistent.md")
        File.WriteAllText(source, "some content")

        let result = syncPair Apply source target

        test <@ result = Error(TargetMissing target) @>)

[<Fact>]
let ``syncPair Apply - full-file sync when no sections in source`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "Full file content\nNo sync tags")
        File.WriteAllText(target, "Old target content")

        let result = syncPair Apply source target

        test <@ result = Ok Updated @>
        test <@ File.ReadAllText(target) = "Full file content\nNo sync tags" @>)

[<Fact>]
let ``syncPair Check - returns InSync for full-file sync when content matches`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "Same content no sync tags")
        File.WriteAllText(target, "Same content no sync tags")

        let result = syncPair Check source target

        test <@ result = Ok InSync @>)

[<Fact>]
let ``syncPair Check - returns OutOfSync for full-file sync when content differs`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "New full file content")
        File.WriteAllText(target, "Old full file content")

        let result = syncPair Check source target

        test <@ result = Ok OutOfSync @>)

[<Fact>]
let ``syncPair Apply - updates section-based content when it differs`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(
            source,
            """<!-- sync:intro:start -->
Updated section content
<!-- sync:intro:end -->"""
        )

        File.WriteAllText(
            target,
            """# Docs page
<!-- sync:intro -->
Old section content
<!-- sync:intro:end -->"""
        )

        let result = syncPair Apply source target
        let updatedTarget = File.ReadAllText(target)

        test <@ result = Ok Updated @>
        test <@ updatedTarget.Contains "Updated section content" @>
        test <@ not (updatedTarget.Contains "Old section content") @>)

[<Fact>]
let ``syncPair Check - returns InSync for section-based content in sync`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(
            source,
            """<!-- sync:intro:start -->
Same content
<!-- sync:intro:end -->"""
        )

        File.WriteAllText(
            target,
            """<!-- sync:intro -->
Same content
<!-- sync:intro:end -->"""
        )

        let result = syncPair Check source target

        test <@ result = Ok InSync @>)

[<Fact>]
let ``syncPair Apply - section-based content with dollar signs`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(
            source,
            """<!-- sync:pricing:start -->
Cost is $50 per $unit
<!-- sync:pricing:end -->"""
        )

        File.WriteAllText(
            target,
            """<!-- sync:pricing -->
Old pricing
<!-- sync:pricing:end -->"""
        )

        let result = syncPair Apply source target
        let updatedTarget = File.ReadAllText(target)

        test <@ result = Ok Updated @>
        test <@ updatedTarget.Contains "Cost is $50 per $unit" @>)

[<Fact>]
let ``syncPair Check - SourceMissing in check mode`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "nonexistent.md")
        let target = Path.Combine(tmpDir, "index.md")
        File.WriteAllText(target, "content")

        let result = syncPair Check source target

        test <@ result = Error(SourceMissing source) @>)

[<Fact>]
let ``syncPair Check - TargetMissing in check mode`` () =
    withTempDir (fun tmpDir ->
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "nonexistent.md")
        File.WriteAllText(source, "content")

        let result = syncPair Check source target

        test <@ result = Error(TargetMissing target) @>)

// --- discoverPairs tests ---

[<Fact>]
let ``discoverPairs - finds README to docs index`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")

        let pairs = discoverPairs tmpDir

        test
            <@
                pairs
                |> List.exists (fun p -> p.Source.EndsWith "README.md" && p.Target.EndsWith "index.md")
            @>)

[<Fact>]
let ``discoverPairs - finds src subdirectory READMEs`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")

        let pairs = discoverPairs tmpDir

        let hasPair =
            pairs
            |> List.exists (fun p ->
                p.Source.Contains "MyLib"
                && p.Source.EndsWith "README.md"
                && p.Target.Contains "MyLib"
                && p.Target.EndsWith "index.md")

        test <@ hasPair @>)

[<Fact>]
let ``discoverPairs - returns empty when no READMEs found`` () =
    withTempDir (fun tmpDir ->
        let pairs = discoverPairs tmpDir
        test <@ pairs.IsEmpty @>)

[<Fact>]
let ``discoverPairs - ignores src dirs where README exists but target does not`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")

        let pairs = discoverPairs tmpDir

        let hasPair = pairs |> List.exists (fun p -> p.Source.Contains "MyLib")

        test <@ not hasPair @>)

[<Fact>]
let ``discoverPairs - empty src directory returns no src pairs`` () =
    withTempDir (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let pairs = discoverPairs tmpDir
        test <@ pairs.IsEmpty @>)

[<Fact>]
let ``discoverPairs - root README only with matching docs index`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")

        let pairs = discoverPairs tmpDir

        test <@ pairs.Length = 1 @>
        test <@ pairs[0].Source.EndsWith "README.md" @>
        test <@ pairs[0].Target.EndsWith "index.md" @>)

[<Fact>]
let ``discoverPairs - multiple src subdirectories`` () =
    withTempDir (fun tmpDir ->
        for name in [ "LibA"; "LibB"; "LibC" ] do
            let srcDir = Path.Combine(tmpDir, "src", name)
            let docsDir = Path.Combine(tmpDir, "docs", name)
            Directory.CreateDirectory(srcDir) |> ignore
            Directory.CreateDirectory(docsDir) |> ignore
            File.WriteAllText(Path.Combine(srcDir, "README.md"), sprintf "%s readme" name)
            File.WriteAllText(Path.Combine(docsDir, "index.md"), sprintf "%s docs" name)

        let pairs = discoverPairs tmpDir

        test <@ pairs.Length = 3 @>)

[<Fact>]
let ``discoverPairs - src with subdirectories lacking READMEs returns no src pairs`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        // Create subdirectories without README files
        Directory.CreateDirectory(Path.Combine(srcDir, "EmptyLib")) |> ignore
        Directory.CreateDirectory(Path.Combine(srcDir, "AnotherLib")) |> ignore

        let pairs = discoverPairs tmpDir
        test <@ pairs.IsEmpty @>)

[<Fact>]
let ``discoverPairs - src with single empty subdirectory returns no pairs`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "OnlyChild")
        Directory.CreateDirectory(srcDir) |> ignore

        let pairs = discoverPairs tmpDir
        test <@ pairs.IsEmpty @>)

// --- discoverWarnings tests ---

[<Fact>]
let ``discoverWarnings - warns when src README exists but docs target missing`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>

        test
            <@
                match warnings[0] with
                | MissingTarget(name, path) -> name = "MyLib" && path.Contains "index.md"
                | _ -> false
            @>)

[<Fact>]
let ``discoverWarnings - warns when docs target exists but src README missing`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>

        test
            <@
                match warnings[0] with
                | MissingSource(name, path) -> name = "MyLib" && path.Contains "README.md"
                | _ -> false
            @>)

[<Fact>]
let ``discoverWarnings - warns when root README exists but docs/index.md missing`` () =
    withTempDir (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>

        test
            <@
                match warnings[0] with
                | MissingTarget(_, path) -> path.Contains "index.md"
                | _ -> false
            @>)

[<Fact>]
let ``discoverWarnings - warns when docs/index.md exists but root README missing`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>

        test
            <@
                match warnings[0] with
                | MissingSource(_, path) -> path.Contains "README.md"
                | _ -> false
            @>)

[<Fact>]
let ``discoverWarnings - no warnings when pairs are complete`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.IsEmpty @>)

[<Fact>]
let ``discoverWarnings - no warnings when nothing exists`` () =
    withTempDir (fun tmpDir ->
        let warnings = discoverWarnings tmpDir
        test <@ warnings.IsEmpty @>)

[<Fact>]
let ``discoverPairsAndWarnings - returns both pairs and warnings together`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")
        // src/MyLib has README but no docs target
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")

        let result = discoverPairsAndWarnings tmpDir

        test <@ result.Pairs.Length = 1 @>
        test <@ result.Warnings.Length = 1 @>)

[<Fact>]
let ``discoverWarnings - MissingTarget variant has correct suggested path`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "Foo")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "foo readme")

        let warnings = discoverWarnings tmpDir

        match warnings with
        | [ MissingTarget(name, suggestedPath) ] ->
            test <@ name = "Foo" @>
            test <@ suggestedPath = Path.Combine("docs", "Foo", "index.md") @>
        | other -> failwithf "Expected single MissingTarget warning, got: %A" other)

[<Fact>]
let ``discoverWarnings - MissingSource variant has correct suggested path`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs", "Bar")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "bar docs")
        let srcDir = Path.Combine(tmpDir, "src", "Bar")
        Directory.CreateDirectory(srcDir) |> ignore

        let warnings = discoverWarnings tmpDir

        match warnings with
        | [ MissingSource(name, suggestedPath) ] ->
            test <@ name = "Bar" @>
            test <@ suggestedPath = Path.Combine("src", "Bar", "README.md") @>
        | other -> failwithf "Expected single MissingSource warning, got: %A" other)

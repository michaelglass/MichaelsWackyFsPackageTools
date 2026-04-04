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

// --- syncPair tests ---

[<Fact>]
let ``syncPair check mode - returns InSync when content matches`` () =
    let tmpDir = createTempDir ()

    try
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

        let result = syncPair true source target

        test <@ result = InSync @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair check mode - returns OutOfSync when content differs`` () =
    let tmpDir = createTempDir ()

    try
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

        let result = syncPair true source target

        test <@ result = OutOfSync @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair - returns SourceMissing when source does not exist`` () =
    let tmpDir = createTempDir ()

    try
        let source = Path.Combine(tmpDir, "nonexistent.md")
        let target = Path.Combine(tmpDir, "index.md")
        File.WriteAllText(target, "some content")

        let result = syncPair false source target

        test <@ result = SourceMissing @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair - returns TargetMissing when target does not exist`` () =
    let tmpDir = createTempDir ()

    try
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "nonexistent.md")
        File.WriteAllText(source, "some content")

        let result = syncPair false source target

        test <@ result = TargetMissing @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair - full-file sync when no sections in source`` () =
    let tmpDir = createTempDir ()

    try
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "Full file content\nNo sync tags")
        File.WriteAllText(target, "Old target content")

        let result = syncPair false source target

        test <@ result = Updated @>
        test <@ File.ReadAllText(target) = "Full file content\nNo sync tags" @>
    finally
        cleanupDir tmpDir

// --- discoverPairs tests ---

[<Fact>]
let ``discoverPairs - finds README to docs index`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")

        let pairs = discoverPairs tmpDir

        test
            <@
                pairs
                |> List.exists (fun (s, t) -> s.EndsWith "README.md" && t.EndsWith "index.md")
            @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverPairs - finds src subdirectory READMEs`` () =
    let tmpDir = createTempDir ()

    try
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")

        let pairs = discoverPairs tmpDir

        let hasPair =
            pairs
            |> List.exists (fun (s, t) ->
                s.Contains "MyLib"
                && s.EndsWith "README.md"
                && t.Contains "MyLib"
                && t.EndsWith "index.md")

        test <@ hasPair @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverPairs - returns empty when no READMEs found`` () =
    let tmpDir = createTempDir ()

    try
        let pairs = discoverPairs tmpDir

        test <@ pairs.IsEmpty @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair check mode - returns InSync for full-file sync when content matches`` () =
    let tmpDir = createTempDir ()

    try
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "Same content no sync tags")
        File.WriteAllText(target, "Same content no sync tags")

        let result = syncPair true source target

        test <@ result = InSync @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair check mode - returns OutOfSync for full-file sync when content differs`` () =
    let tmpDir = createTempDir ()

    try
        let source = Path.Combine(tmpDir, "README.md")
        let target = Path.Combine(tmpDir, "index.md")

        File.WriteAllText(source, "New full file content")
        File.WriteAllText(target, "Old full file content")

        let result = syncPair true source target

        test <@ result = OutOfSync @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``syncPair sync mode - updates section-based content when it differs`` () =
    let tmpDir = createTempDir ()

    try
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

        let result = syncPair false source target
        let updatedTarget = File.ReadAllText(target)

        test <@ result = Updated @>
        test <@ updatedTarget.Contains "Updated section content" @>
        test <@ not (updatedTarget.Contains "Old section content") @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverPairs - ignores src dirs where README exists but target does not`` () =
    let tmpDir = createTempDir ()

    try
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")

        let pairs = discoverPairs tmpDir

        let hasPair = pairs |> List.exists (fun (s, _) -> s.Contains "MyLib")

        test <@ not hasPair @>
    finally
        cleanupDir tmpDir

// --- discoverWarnings tests ---

[<Fact>]
let ``discoverWarnings - warns when src README exists but docs target missing`` () =
    let tmpDir = createTempDir ()

    try
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>
        test <@ warnings[0].Contains "MyLib" @>
        test <@ warnings[0].Contains "docs/MyLib/index.md" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverWarnings - warns when docs target exists but src README missing`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        // No README.md created
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>
        test <@ warnings[0].Contains "MyLib" @>
        test <@ warnings[0].Contains "src/MyLib/README.md" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverWarnings - warns when root README exists but docs/index.md missing`` () =
    let tmpDir = createTempDir ()

    try
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>
        test <@ warnings[0].Contains "docs/index.md" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverWarnings - warns when docs/index.md exists but root README missing`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.Length = 1 @>
        test <@ warnings[0].Contains "README.md" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverWarnings - no warnings when pairs are complete`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root readme")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "docs index")
        let warnings = discoverWarnings tmpDir
        test <@ warnings.IsEmpty @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``discoverWarnings - no warnings when nothing exists`` () =
    let tmpDir = createTempDir ()

    try
        let warnings = discoverWarnings tmpDir
        test <@ warnings.IsEmpty @>
    finally
        cleanupDir tmpDir

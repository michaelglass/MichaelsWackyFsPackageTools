module SyncDocs.Tests.CodeRegionTests

open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open SyncDocs.Sync
open Tests.Common.TestHelpers

// --- extractRegion tests ---

[<Fact>]
let ``extractRegion - extracts lines between comment markers`` () =
    let fileContent =
        """module Foo

// sync:example:start
let answer = 42
printfn "%d" answer
// sync:example:end

let other = 1
"""

    let result = extractRegion fileContent "example"

    test <@ result = Ok [ "let answer = 42"; "printfn \"%d\" answer" ] @>

[<Fact>]
let ``extractRegion - strips common leading indentation`` () =
    let fileContent =
        """type T() =
    // sync:method:start
    member _.Run() =
        printfn "running"
    // sync:method:end
"""

    let result = extractRegion fileContent "method"

    test <@ result = Ok [ "member _.Run() ="; "    printfn \"running\"" ] @>

[<Fact>]
let ``extractRegion - dedents tabs consistently`` () =
    let fileContent =
        "\t\t// sync:tabbed:start\n\t\tlet x = 1\n\t\t\tlet y = 2\n\t\t// sync:tabbed:end\n"

    let result = extractRegion fileContent "tabbed"

    test <@ result = Ok [ "let x = 1"; "\tlet y = 2" ] @>

[<Fact>]
let ``extractRegion - preserves blank lines without counting them for dedent`` () =
    let fileContent =
        """    // sync:blanks:start
    let a = 1

    let b = 2
    // sync:blanks:end
"""

    let result = extractRegion fileContent "blanks"

    test <@ result = Ok [ "let a = 1"; ""; "let b = 2" ] @>

[<Fact>]
let ``extractRegion - empty region returns empty list`` () =
    let fileContent = "// sync:empty:start\n// sync:empty:end\n"

    let result = extractRegion fileContent "empty"

    test <@ result = Ok [] @>

[<Fact>]
let ``extractRegion - missing region returns RegionMissing`` () =
    let fileContent = "let x = 1\n// sync:other:start\nlet y = 2\n// sync:other:end\n"

    let result = extractRegion fileContent "absent"

    test <@ result = Error(RegionMissing "absent") @>

[<Fact>]
let ``extractRegion - duplicate start markers returns RegionDuplicated`` () =
    let fileContent =
        """// sync:dup:start
let a = 1
// sync:dup:end
// sync:dup:start
let b = 2
// sync:dup:end
"""

    let result = extractRegion fileContent "dup"

    test <@ result = Error(RegionDuplicated "dup") @>

[<Fact>]
let ``extractRegion - start without matching end returns RegionUnterminated`` () =
    let fileContent = "// sync:open:start\nlet a = 1\n"

    let result = extractRegion fileContent "open"

    test <@ result = Error(RegionUnterminated "open") @>

[<Fact>]
let ``extractRegion - end before start returns RegionUnterminated`` () =
    let fileContent = "// sync:swapped:end\nlet a = 1\n// sync:swapped:start\n"

    let result = extractRegion fileContent "swapped"

    test <@ result = Error(RegionUnterminated "swapped") @>

[<Fact>]
let ``extractRegion - duplicate end markers returns RegionDuplicated`` () =
    let fileContent =
        "// sync:dupend:start\nlet a = 1\n// sync:dupend:end\n// sync:dupend:end\n"

    let result = extractRegion fileContent "dupend"

    test <@ result = Error(RegionDuplicated "dupend") @>

// --- renderCodeBlock tests ---

[<Fact>]
let ``renderCodeBlock - wraps lines in fsharp fence with surrounding newlines`` () =
    let body = renderCodeBlock [ "let x = 1"; "let y = 2" ]

    test <@ body = "\n```fsharp\nlet x = 1\nlet y = 2\n```\n" @>

[<Fact>]
let ``renderCodeBlock - empty lines still produce a valid fence`` () =
    let body = renderCodeBlock []

    test <@ body = "\n```fsharp\n\n```\n" @>

// --- syncCodeRegions tests ---

let private writeFile dir relPath (content: string) =
    let full = Path.Combine(dir, relPath)
    Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
    File.WriteAllText(full, content)
    full

[<Fact>]
let ``syncCodeRegions Check - InSync when README block already matches region`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 1\n// sync:demo:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "intro\n<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\nend\n"

        let result = syncCodeRegions Check tmpDir readme

        test <@ result = Ok InSync @>)

[<Fact>]
let ``syncCodeRegions Check - OutOfSync when README block differs from region`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 2\n// sync:demo:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        let result = syncCodeRegions Check tmpDir readme

        test <@ result = Ok OutOfSync @>)

[<Fact>]
let ``syncCodeRegions Apply - rewrites README block from region`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 99\n// sync:demo:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>
        test <@ updated.Contains "let x = 99" @>
        test <@ not (updated.Contains "let x = 1") @>
        // marker line preserved with its src attribute
        test <@ updated.Contains "<!-- sync:demo:start src=code/Snippets.fs -->" @>
        test <@ updated.Contains "```fsharp" @>)

[<Fact>]
let ``syncCodeRegions Apply - dedents region body when rewriting`` () =
    withTempDir (fun tmpDir ->
        writeFile
            tmpDir
            "code/Snippets.fs"
            "type T() =\n    // sync:m:start\n    member _.Go() = 1\n    // sync:m:end\n"
        |> ignore

        let readme =
            writeFile tmpDir "README.md" "<!-- sync:m:start src=code/Snippets.fs -->\nold\n<!-- sync:m:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>
        test <@ updated.Contains "\n```fsharp\nmember _.Go() = 1\n```\n" @>)

[<Fact>]
let ``syncCodeRegions - explicit region override via hash`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:realRegion:start\nlet picked = true\n// sync:realRegion:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:blockName:start src=code/Snippets.fs#realRegion -->\nold\n<!-- sync:blockName:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>
        test <@ updated.Contains "let picked = true" @>)

[<Fact>]
let ``syncCodeRegions Apply - preserves dollar signs in code body`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:money:start\nlet price = \"$100\"\n// sync:money:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:money:start src=code/Snippets.fs -->\nold\n<!-- sync:money:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>
        test <@ updated.Contains "let price = \"$100\"" @>)

[<Fact>]
let ``syncCodeRegions - missing source file returns CodeFileMissing`` () =
    withTempDir (fun tmpDir ->
        let readme =
            writeFile tmpDir "README.md" "<!-- sync:demo:start src=code/Nope.fs -->\nold\n<!-- sync:demo:end -->\n"

        let result = syncCodeRegions Check tmpDir readme

        test
            <@
                match result with
                | Error(CodeFileMissing path) -> path.Contains "Nope.fs"
                | _ -> false
            @>)

[<Fact>]
let ``syncCodeRegions - missing region in file returns RegionError`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "let unrelated = 1\n" |> ignore

        let readme =
            writeFile tmpDir "README.md" "<!-- sync:demo:start src=code/Snippets.fs -->\nold\n<!-- sync:demo:end -->\n"

        let result = syncCodeRegions Check tmpDir readme

        test
            <@
                match result with
                | Error(CodeRegionError(_, RegionMissing "demo")) -> true
                | _ -> false
            @>)

[<Fact>]
let ``syncCodeRegions - duplicate region in file returns RegionError`` () =
    withTempDir (fun tmpDir ->
        writeFile
            tmpDir
            "code/Snippets.fs"
            "// sync:demo:start\nlet a = 1\n// sync:demo:end\n// sync:demo:start\nlet b = 2\n// sync:demo:end\n"
        |> ignore

        let readme =
            writeFile tmpDir "README.md" "<!-- sync:demo:start src=code/Snippets.fs -->\nold\n<!-- sync:demo:end -->\n"

        let result = syncCodeRegions Check tmpDir readme

        test
            <@
                match result with
                | Error(CodeRegionError(_, RegionDuplicated "demo")) -> true
                | _ -> false
            @>)

[<Fact>]
let ``syncCodeRegions - README with no code-sourced blocks is InSync`` () =
    withTempDir (fun tmpDir ->
        let readme =
            writeFile tmpDir "README.md" "<!-- sync:intro:start -->\nordinary text section\n<!-- sync:intro:end -->\n"

        let result = syncCodeRegions Check tmpDir readme

        test <@ result = Ok InSync @>)

[<Fact>]
let ``syncCodeRegions Apply - leaves ordinary text sections untouched`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 7\n// sync:demo:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:intro:start -->\nplain text\n<!-- sync:intro:end -->\n<!-- sync:demo:start src=code/Snippets.fs -->\nold\n<!-- sync:demo:end -->\n"

        syncCodeRegions Apply tmpDir readme |> ignore
        let updated = File.ReadAllText(readme)

        // The text section's start marker (no src) is unchanged and its body preserved
        test <@ updated.Contains "<!-- sync:intro:start -->\nplain text\n<!-- sync:intro:end -->" @>
        test <@ updated.Contains "let x = 7" @>)

[<Fact>]
let ``syncCodeRegions - file with multiple code blocks all resolved`` () =
    withTempDir (fun tmpDir ->
        writeFile
            tmpDir
            "code/Snippets.fs"
            "// sync:one:start\nlet a = 1\n// sync:one:end\n// sync:two:start\nlet b = 2\n// sync:two:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:one:start src=code/Snippets.fs -->\nold\n<!-- sync:one:end -->\n<!-- sync:two:start src=code/Snippets.fs -->\nold\n<!-- sync:two:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>
        test <@ updated.Contains "let a = 1" @>
        test <@ updated.Contains "let b = 2" @>)

[<Fact>]
let ``syncCodeRegions Apply - empty region renders an empty fence`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:hollow:start\n// sync:hollow:end\n"
        |> ignore

        let readme =
            writeFile
                tmpDir
                "README.md"
                "<!-- sync:hollow:start src=code/Snippets.fs -->\nold\n<!-- sync:hollow:end -->\n"

        let result = syncCodeRegions Apply tmpDir readme
        let updated = File.ReadAllText(readme)

        test <@ result = Ok Updated @>

        test
            <@
                updated.Contains
                    "<!-- sync:hollow:start src=code/Snippets.fs -->\n```fsharp\n\n```\n<!-- sync:hollow:end -->"
            @>)

[<Fact>]
let ``syncCodeRegions Apply - block with start marker but no end is treated as empty current body`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "code/Snippets.fs" "// sync:lonely:start\nlet z = 5\n// sync:lonely:end\n"
        |> ignore

        // The README's code block has a start marker (with src) but no end marker,
        // so its current body cannot be located and is treated as empty.
        let readme =
            writeFile tmpDir "README.md" "<!-- sync:lonely:start src=code/Snippets.fs -->\nstill here\n"

        let result = syncCodeRegions Apply tmpDir readme

        // Region resolves fine, current body is empty (None), so the block counts
        // as changed; nothing is rewritten because no end marker delimits it.
        test <@ result = Ok Updated @>)

// --- discoverStandaloneCodeDocs tests ---

[<Fact>]
let ``discoverStandaloneCodeDocs - finds a markdown doc carrying a src= block`` () =
    withTempDir (fun tmpDir ->
        writeFile
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:ex:start src=code/Snippets.fs -->\nbody\n<!-- sync:ex:end -->\n"
        |> ignore

        let docs = discoverStandaloneCodeDocs tmpDir []

        test <@ docs |> List.exists (fun p -> p.EndsWith "writing-plugins.md") @>)

[<Fact>]
let ``discoverStandaloneCodeDocs - ignores markdown without a src= block`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "docs/plain.md" "<!-- sync:intro:start -->\njust text\n<!-- sync:intro:end -->\n"
        |> ignore

        writeFile tmpDir "notes.md" "# heading\nno markers at all\n" |> ignore

        let docs = discoverStandaloneCodeDocs tmpDir []

        test <@ docs.IsEmpty @>)

[<Fact>]
let ``discoverStandaloneCodeDocs - excludes paths listed as pair sources`` () =
    withTempDir (fun tmpDir ->
        let readme =
            writeFile tmpDir "README.md" "<!-- sync:ex:start src=code/Snippets.fs -->\nbody\n<!-- sync:ex:end -->\n"

        let guide =
            writeFile
                tmpDir
                "docs/guide.md"
                "<!-- sync:ex:start src=code/Snippets.fs -->\nbody\n<!-- sync:ex:end -->\n"

        let docs = discoverStandaloneCodeDocs tmpDir [ readme ]

        // README is a pair source -> excluded; the standalone guide remains
        test <@ not (docs |> List.exists (fun p -> p = readme)) @>
        test <@ docs |> List.exists (fun p -> p = guide) @>)

[<Fact>]
let ``discoverStandaloneCodeDocs - skips bin obj and dot directories`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "bin/built.md" "<!-- sync:ex:start src=x.fs -->\nb\n<!-- sync:ex:end -->\n"
        |> ignore

        writeFile tmpDir "obj/gen.md" "<!-- sync:ex:start src=x.fs -->\nb\n<!-- sync:ex:end -->\n"
        |> ignore

        writeFile tmpDir ".git/hook.md" "<!-- sync:ex:start src=x.fs -->\nb\n<!-- sync:ex:end -->\n"
        |> ignore

        writeFile tmpDir ".jj/op.md" "<!-- sync:ex:start src=x.fs -->\nb\n<!-- sync:ex:end -->\n"
        |> ignore

        writeFile tmpDir "node_modules/pkg/readme.md" "<!-- sync:ex:start src=x.fs -->\nb\n<!-- sync:ex:end -->\n"
        |> ignore

        let docs = discoverStandaloneCodeDocs tmpDir []

        test <@ docs.IsEmpty @>)

[<Fact>]
let ``discoverStandaloneCodeDocs - returns empty when no docs carry src= blocks`` () =
    withTempDir (fun tmpDir ->
        writeFile tmpDir "README.md" "# plain\n" |> ignore
        let docs = discoverStandaloneCodeDocs tmpDir []
        test <@ docs.IsEmpty @>)

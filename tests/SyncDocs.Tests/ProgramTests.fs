module SyncDocs.Tests.ProgramTests

open System.IO
open Xunit
open Tests.Common
open Swensen.Unquote
open SyncDocs.Program
open Tests.Common.TestHelpers

[<Fact>]
let ``run - check prints warnings for incomplete pairs`` () =
    withTempDir (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        // No docs/MyLib/index.md -- should warn

        let printed, result = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ result = Ok 0 @>
        test <@ printed.Contains "MyLib" @>
        test <@ printed.Contains "index.md" @>)

[<Fact>]
let ``run - invalid command returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "bogus" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - no args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [||] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - check with no pairs returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with in-sync pair returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with out-of-sync pair returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - sync updates files and returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Updated content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(Path.Combine(docsDir, "index.md")) = "Updated content" @>)

[<Fact>]
let ``run - sync with in-sync pair returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``main - no args returns 1`` () =
    let result = main [||]
    test <@ result = 1 @>

[<Fact>]
let ``main - bogus command returns 1`` () =
    let result = main [| "bogus" |]
    test <@ result = 1 @>

[<Fact>]
let ``main - check with no pairs in CWD returns 0`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check returns Ok 1 when files are out of sync`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "old content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - sync with multiple pairs processes all`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "old root content")

        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        let libDocsDir = Path.Combine(docsDir, "Lib")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(libDocsDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib content")
        File.WriteAllText(Path.Combine(libDocsDir, "index.md"), "old lib content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(Path.Combine(docsDir, "index.md")) = "root content" @>
        test <@ File.ReadAllText(Path.Combine(libDocsDir, "index.md")) = "lib content" @>)

[<Fact>]
let ``run - check prints in sync message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same")

        let printed, _ = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ printed.Contains "in sync" @>)

[<Fact>]
let ``run - sync prints updated message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old")

        let printed, _ = withCapturedConsole (fun () -> run [| "sync" |] tmpDir)
        test <@ printed.Contains "updated" @>)

[<Fact>]
let ``run - check prints OUT OF SYNC message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old")

        let printed, _ = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ printed.Contains "OUT OF SYNC" @>)

[<Fact>]
let ``run - check prints no pairs found message`` () =
    withTempDir (fun tmpDir ->
        let printed, _ = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ printed.Contains "No README.md -> docs/ pairs found" @>)

[<Fact>]
let ``run - error message contains usage text`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "bogus" |] tmpDir

        match result with
        | Error msg -> test <@ msg.Contains "Usage" @>
        | Ok _ -> failwith "Expected Error")

[<Fact>]
let ``main - check returns 0 for empty dir`` () =
    // main uses CWD, but we can verify via run
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check prints MissingSource warning when docs target exists but source README missing`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let printed, result = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ result = Ok 0 @>
        test <@ printed.Contains "Warning" @>
        test <@ printed.Contains "MyLib" @>
        test <@ printed.Contains "README.md" @>)

[<Fact>]
let ``main - returns 0 for successful run`` () =
    withTempDir (fun tmpDir ->
        let prev = System.IO.Directory.GetCurrentDirectory()

        try
            System.IO.Directory.SetCurrentDirectory(tmpDir)
            let result = main [| "check" |]
            test <@ result = 0 @>
        finally
            System.IO.Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``run - multiple args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "check"; "extra" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - two word command returns Error with usage`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "sync"; "check" |] tmpDir

        match result with
        | Error msg -> test <@ msg.Contains "Usage" @>
        | Ok _ -> failwith "Expected Error")

[<Fact>]
let ``run - three args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "a"; "b"; "c" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - null argv returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Unchecked.defaultof<string array>) tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - sync prints in sync message for already synced pair`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let printed, _ = withCapturedConsole (fun () -> run [| "sync" |] tmpDir)
        test <@ printed.Contains "in sync" @>)

// --- code-sourced block integration tests ---

let private write dir relPath (content: string) =
    let full = Path.Combine(dir, relPath)
    Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
    File.WriteAllText(full, content)

[<Fact>]
let ``run - check returns Ok 1 when README code block drifted from source file`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 2\n// sync:demo:end\n"

        write
            tmpDir
            "README.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        write tmpDir "docs/index.md" "placeholder"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - check returns Ok 0 when README code block matches source file`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 1\n// sync:demo:end\n"

        write
            tmpDir
            "README.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        write tmpDir "docs/index.md" "placeholder"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check returns Ok 1 when a code source file is missing`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "README.md" "<!-- sync:demo:start src=code/Gone.fs -->\nold\n<!-- sync:demo:end -->\n"
        write tmpDir "docs/index.md" "placeholder"

        let printed, result = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ result = Ok 1 @>
        test <@ printed.Contains "Gone.fs" @>)

[<Fact>]
let ``run - sync refreshes README code block then propagates to docs`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet answer = 42\n// sync:demo:end\n"

        // README's code block is stale; the text block is also mirrored to docs
        write
            tmpDir
            "README.md"
            ("<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet answer = 0\n```\n<!-- sync:demo:end -->\n"
             + "<!-- sync:guide:start -->\nhow to use\n<!-- sync:guide:end -->\n")

        write
            tmpDir
            "docs/index.md"
            ("<!-- sync:demo -->\nstale docs\n<!-- sync:demo:end -->\n"
             + "<!-- sync:guide -->\nstale docs\n<!-- sync:guide:end -->\n")

        let result = run [| "sync" |] tmpDir

        let readme = File.ReadAllText(Path.Combine(tmpDir, "README.md"))
        let docs = File.ReadAllText(Path.Combine(tmpDir, "docs", "index.md"))

        test <@ result = Ok 0 @>
        // README code block refreshed from the file
        test <@ readme.Contains "let answer = 42" @>
        test <@ not (readme.Contains "let answer = 0") @>
        // docs picked up BOTH the refreshed code block and the text block
        test <@ docs.Contains "let answer = 42" @>
        test <@ docs.Contains "how to use" @>)

[<Fact>]
let ``run - check passes when only ordinary text sections present (no regression)`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "README.md" "<!-- sync:intro:start -->\nhello\n<!-- sync:intro:end -->\n"

        write tmpDir "docs/index.md" "<!-- sync:intro -->\nhello\n<!-- sync:intro:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

// --- standalone-doc code-region integration tests ---

[<Fact>]
let ``run - check returns Ok 0 when a standalone doc code block matches its source`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 1\n// sync:demo:end\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check returns Ok 1 when a standalone doc code block drifted from its source`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 2\n// sync:demo:end\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        let printed, result = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ result = Ok 1 @>
        test <@ printed.Contains "writing-plugins.md" @>)

[<Fact>]
let ``run - sync refreshes a standalone doc code block in place`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet answer = 42\n// sync:demo:end\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet answer = 0\n```\n<!-- sync:demo:end -->\n"

        let result = run [| "sync" |] tmpDir
        let doc = File.ReadAllText(Path.Combine(tmpDir, "docs", "writing-plugins.md"))

        test <@ result = Ok 0 @>
        test <@ doc.Contains "let answer = 42" @>
        test <@ not (doc.Contains "let answer = 0") @>)

[<Fact>]
let ``run - check returns Ok 1 when a standalone doc references a missing source file`` () =
    withTempDir (fun tmpDir ->
        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Gone.fs -->\nold\n<!-- sync:demo:end -->\n"

        let printed, result = withCapturedConsole (fun () -> run [| "check" |] tmpDir)
        test <@ result = Ok 1 @>
        test <@ printed.Contains "Gone.fs" @>)

[<Fact>]
let ``run - check returns Ok 1 when a standalone doc references a missing region`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "code/Snippets.fs" "let unrelated = 1\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\nold\n<!-- sync:demo:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - check handles a standalone doc and a README pair together`` () =
    withTempDir (fun tmpDir ->
        // README pair: code block IN SYNC
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 1\n// sync:demo:end\n"

        write
            tmpDir
            "README.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        write tmpDir "docs/index.md" "placeholder"

        // Standalone guide: code block DRIFTED -> whole check must fail
        write tmpDir "guide/Other.fs" "// sync:g:start\nlet y = 9\n// sync:g:end\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:g:start src=guide/Other.fs -->\n```fsharp\nlet y = 0\n```\n<!-- sync:g:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - sync does not double-process a path that is both a pair source and a src= carrier`` () =
    withTempDir (fun tmpDir ->
        // README is a pair source AND carries a src= block. It must be processed
        // exactly once (as the pair source), never again as a standalone doc.
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 7\n// sync:demo:end\n"

        write
            tmpDir
            "README.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 0\n```\n<!-- sync:demo:end -->\n"

        write tmpDir "docs/index.md" "placeholder"

        let printed, result = withCapturedConsole (fun () -> run [| "sync" |] tmpDir)
        let readme = File.ReadAllText(Path.Combine(tmpDir, "README.md"))

        test <@ result = Ok 0 @>
        test <@ readme.Contains "let x = 7" @>
        // README appears only once in the "code regions updated" output (single processing)
        let occurrences =
            (printed.Length - printed.Replace("README.md: code regions updated", "").Length)
            / "README.md: code regions updated".Length

        test <@ occurrences = 1 @>)

[<Fact>]
let ``run - check is Ok 0 in a repo with a pair but no standalone src= docs`` () =
    withTempDir (fun tmpDir ->
        write tmpDir "README.md" "<!-- sync:intro:start -->\nhi\n<!-- sync:intro:end -->\n"
        write tmpDir "docs/index.md" "<!-- sync:intro -->\nhi\n<!-- sync:intro:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check processes a standalone doc even when there are no pairs`` () =
    withTempDir (fun tmpDir ->
        // No README.md / docs/index.md pair at all, only a standalone guide.
        write tmpDir "code/Snippets.fs" "// sync:demo:start\nlet x = 2\n// sync:demo:end\n"

        write
            tmpDir
            "docs/writing-plugins.md"
            "<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\nlet x = 1\n```\n<!-- sync:demo:end -->\n"

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

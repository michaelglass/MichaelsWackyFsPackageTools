module SyncDocs.Tests.LineEndingTests

open System.IO
open Xunit
open Swensen.Unquote
open SyncDocs.Sync
open Tests.Common.TestHelpers

let private crlf (s: string) = s.Replace("\n", "\r\n")

let private isAllCrlf (s: string) =
    s.Contains "\r\n" && not (s.Replace("\r\n", "").Contains "\n")

let private isAllLf (s: string) =
    s.Contains "\n" && not (s.Contains "\r")

let private lfDoc =
    "# Docs\n<!-- sync:intro -->\nOld intro content\n<!-- sync:intro:end -->\n"

let private lfReadme =
    "# Readme\n<!-- sync:intro:start -->\nNew intro content\n<!-- sync:intro:end -->\n"

let private writePair dir (readme: string) (doc: string) =
    let source = Path.Combine(dir, "README.md")
    let target = Path.Combine(dir, "index.md")
    File.WriteAllText(source, readme)
    File.WriteAllText(target, doc)
    source, target

// --- replaceSections ---

[<Fact>]
let ``replaceSections - replaces a section in a CRLF document and keeps CRLF`` () =
    let result =
        replaceSections (crlf lfDoc) (Map.ofList [ "intro", "\nNew intro content\n" ])

    test <@ result.Contains "New intro content" @>
    test <@ not (result.Contains "Old intro content") @>
    test <@ isAllCrlf result @>

[<Fact>]
let ``replaceSections - CRLF section content written into an LF document uses LF`` () =
    let result =
        replaceSections lfDoc (Map.ofList [ "intro", "\r\nNew\r\ncontent\r\n" ])

    test <@ result.Contains "New\ncontent" @>
    test <@ isAllLf result @>

[<Fact>]
let ``replaceSections - mixed document uses its majority line ending and leaves other lines alone`` () =
    // Three CRLF breaks, two LF breaks: the replaced body is CRLF, the LF lines outside it stay LF.
    let doc = "a\nb\n<!-- sync:intro -->\r\nOld\r\n<!-- sync:intro:end -->\r\n"

    let result = replaceSections doc (Map.ofList [ "intro", "\nX\nY\n" ])

    test <@ result = "a\nb\n<!-- sync:intro -->\r\nX\r\nY\r\n<!-- sync:intro:end -->\r\n" @>

[<Fact>]
let ``replaceSections - a line-ending tie counts as LF`` () =
    let doc = "<!-- sync:intro -->\r\nOld\n<!-- sync:intro:end -->"

    let result = replaceSections doc (Map.ofList [ "intro", "\r\nX\r\n" ])

    test <@ result = "<!-- sync:intro -->\nX\n<!-- sync:intro:end -->" @>

// --- syncPair (sections) ---

[<Fact>]
let ``syncPair Check - unsynced CRLF doc is OutOfSync, not a vacuous InSync`` () =
    withTempDir (fun tmpDir ->
        let source, target = writePair tmpDir lfReadme (crlf lfDoc)

        test <@ syncPair Check source target = Ok OutOfSync @>)

[<Fact>]
let ``syncPair Apply - CRLF doc is synced from an LF README and stays CRLF`` () =
    withTempDir (fun tmpDir ->
        let source, target = writePair tmpDir lfReadme (crlf lfDoc)

        let result = syncPair Apply source target
        let written = File.ReadAllText target

        test <@ result = Ok Updated @>
        test <@ written.Contains "New intro content" @>
        test <@ isAllCrlf written @>
        test <@ syncPair Check source target = Ok InSync @>)

[<Fact>]
let ``syncPair Apply - LF doc synced from a CRLF README stays LF`` () =
    withTempDir (fun tmpDir ->
        let source, target = writePair tmpDir (crlf lfReadme) lfDoc

        let result = syncPair Apply source target
        let written = File.ReadAllText target

        test <@ result = Ok Updated @>
        test <@ written.Contains "New intro content" @>
        test <@ isAllLf written @>
        test <@ syncPair Check source target = Ok InSync @>)

// --- syncPair (full file) ---

[<Fact>]
let ``syncPair Check - full-file sync ignores a line-ending-only difference`` () =
    withTempDir (fun tmpDir ->
        let source, target = writePair tmpDir (crlf "plain\nreadme\n") "plain\nreadme\n"

        test <@ syncPair Check source target = Ok InSync @>)

[<Fact>]
let ``syncPair Apply - full-file sync writes the target's line endings`` () =
    withTempDir (fun tmpDir ->
        let source, target = writePair tmpDir "new\nreadme\n" (crlf "old\nreadme\n")

        let result = syncPair Apply source target

        test <@ result = Ok Updated @>
        test <@ File.ReadAllText target = crlf "new\nreadme\n" @>)

// --- syncCodeRegions ---

let private writeCodeFixture dir (code: string) (readme: string) =
    let codeDir = Path.Combine(dir, "code")
    Directory.CreateDirectory codeDir |> ignore
    File.WriteAllText(Path.Combine(codeDir, "Snippets.fs"), code)
    let readmePath = Path.Combine(dir, "README.md")
    File.WriteAllText(readmePath, readme)
    readmePath

let private codeReadme body =
    sprintf "intro\n<!-- sync:demo:start src=code/Snippets.fs -->\n```fsharp\n%s\n```\n<!-- sync:demo:end -->\n" body

[<Fact>]
let ``syncCodeRegions Check - CRLF README matching its region is InSync`` () =
    withTempDir (fun tmpDir ->
        let readme =
            writeCodeFixture tmpDir "// sync:demo:start\nlet x = 1\n// sync:demo:end\n" (crlf (codeReadme "let x = 1"))

        test <@ syncCodeRegions Check tmpDir readme = Ok InSync @>)

[<Fact>]
let ``syncCodeRegions Apply - CRLF README is refreshed and stays CRLF`` () =
    withTempDir (fun tmpDir ->
        let readme =
            writeCodeFixture
                tmpDir
                (crlf "// sync:demo:start\nlet x = 2\nlet y = 3\n// sync:demo:end\n")
                (crlf (codeReadme "let x = 1"))

        let result = syncCodeRegions Apply tmpDir readme

        test <@ result = Ok Updated @>
        test <@ File.ReadAllText readme = crlf (codeReadme "let x = 2\nlet y = 3") @>)

module CoverageRatchet.Core.Tests.CoberturaTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Cobertura

[<Fact>]
let ``parseXml - single file with line coverage`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Foo.fs">
                  <lines>
                    <line number="1" hits="1" />
                    <line number="2" hits="0" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Foo.fs" @>
    test <@ result.[0].LinePct = 50.0 @>

[<Fact>]
let ``parseXml - file with branch coverage via condition-coverage`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Bar.fs">
                  <lines>
                    <line number="1" hits="1" condition-coverage="50% (1/2)" />
                    <line number="2" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].BranchesCovered = 1 @>
    test <@ result.[0].BranchesTotal = 2 @>
    test <@ result.[0].BranchPct = 50.0 @>

[<Fact>]
let ``parseXml - only fs files included`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Foo.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/Bar.cs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Foo.fs" @>

[<Fact>]
let ``parseXml - exclude Test AssemblyInfo AssemblyAttributes`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/MyTest.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/AssemblyInfo.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/Real.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Real.fs" @>

[<Fact>]
let ``parseXml - excludes vendor paths`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/paket-files/github.com/somelib/Lib.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/vendor/ThirdParty.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/MyCode.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "MyCode.fs" @>

[<Fact>]
let ``parseXml - no branches means 100 percent branch coverage`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Simple.fs">
                  <lines>
                    <line number="1" hits="1" />
                    <line number="2" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].BranchPct = 100.0 @>
    test <@ result.[0].BranchesCovered = 0 @>
    test <@ result.[0].BranchesTotal = 0 @>

[<Fact>]
let ``parseXml - multiple classes for same file dedup lines`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Baz.fs">
                  <lines>
                    <line number="1" hits="0" />
                    <line number="2" hits="1" />
                  </lines>
                </class>
                <class filename="/src/Baz.fs">
                  <lines>
                    <line number="1" hits="1" />
                    <line number="3" hits="0" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Baz.fs" @>
    // Lines: 1 (hit via second class), 2 (hit), 3 (not hit) => 2/3
    test <@ Math.Round(result.[0].LinePct, 1) = 66.7 @>

[<Fact>]
let ``parseXmls - merges line coverage across XMLs for same file`` () =
    let xml1 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Foo.fs">
              <lines>
                <line number="1" hits="1" />
                <line number="2" hits="0" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let xml2 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Foo.fs">
              <lines>
                <line number="1" hits="0" />
                <line number="2" hits="1" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let result = parseXmls [ xml1; xml2 ]

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Foo.fs" @>
    test <@ result.[0].LinePct = 100.0 @>

[<Fact>]
let ``findCoverageFiles - returns all XMLs in directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let subDir1 = Path.Combine(tmpDir, "ProjectA")
    let subDir2 = Path.Combine(tmpDir, "ProjectB")
    Directory.CreateDirectory(subDir1) |> ignore
    Directory.CreateDirectory(subDir2) |> ignore

    let xml1 = Path.Combine(subDir1, "coverage.cobertura.xml")
    let xml2 = Path.Combine(subDir2, "coverage.cobertura.xml")
    File.WriteAllText(xml1, "<coverage/>")
    File.WriteAllText(xml2, "<coverage/>")

    try
        let result = findCoverageFiles tmpDir

        test <@ result.Length = 2 @>
        test <@ result |> List.contains xml1 @>
        test <@ result |> List.contains xml2 @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``findCoverageFiles - returns empty for missing directory`` () =
    let result = findCoverageFiles "/nonexistent/path/does/not/exist"
    test <@ List.isEmpty result @>

[<Fact>]
let ``buildBranchGaps - returns uncovered branches per file`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Branchy.fs">
              <lines>
                <line number="10" hits="1" condition-coverage="50% (1/2)" />
                <line number="20" hits="1" condition-coverage="100% (2/2)" />
                <line number="30" hits="1" condition-coverage="25% (1/4)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let rawLines = extractRawLines xml
    let result = buildBranchGaps rawLines

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Branchy.fs" @>
    test <@ result.[0].Gaps.Length = 2 @>

[<Fact>]
let ``buildBranchGaps - file with no uncovered branches not included`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Clean.fs">
              <lines>
                <line number="1" hits="1" condition-coverage="100% (2/2)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let rawLines = extractRawLines xml
    let result = buildBranchGaps rawLines

    test <@ List.isEmpty result @>

// --- ADR 0019 stability repro: the numerator survives what breaks the ratio ---
//
// ADR 0019's finding, restated: pooling a project that covers NONE of a file
// enlarges that file's emitted-line set (the percentage denominator) without
// adding hits. Embeddings.fs read 383/412 = 93.0% alone and 383/639 = 59.9%
// pooled — "same hits, different denominator".
//
// These tests demonstrate that on real parsing rather than asserting it.

let private classXml (fileName: string) (lines: (int * int) list) =
    let lineEls =
        lines
        |> List.map (fun (num, hits) -> sprintf """<line number="%d" hits="%d" branch="false" />""" num hits)
        |> String.concat ""

    sprintf
        """<?xml version="1.0" encoding="utf-8"?><coverage><packages><package name="p"><classes><class name="C" filename="%s"><lines>%s</lines></class></classes></package></packages></coverage>"""
        fileName
        lineEls

[<Fact>]
let ``pooling a project that covers none of a file leaves LinesCovered untouched`` () =
    // Run A emits lines 1-4, hitting 3 of them.
    let runA = classXml "Foo.fs" [ 1, 1; 2, 1; 3, 1; 4, 0 ]

    // Run B emits a WIDER set (1-10) for the same file and hits none of it —
    // the "project that tests nothing of the file" from ADR 0019.
    let runB = classXml "Foo.fs" [ for i in 1..10 -> i, 0 ]

    let alone = parseXmls [ runA ] |> List.head
    let pooled = parseXmls [ runA; runB ] |> List.head

    // The denominator moves...
    test <@ alone.LinesTotal = 4 @>
    test <@ pooled.LinesTotal = 10 @>

    // ...and drags the percentage down with it, on identical hits.
    test <@ alone.LinePct = 75.0 @>
    test <@ pooled.LinePct = 30.0 @>

    // But the numerator — what count floors gate on — does not move.
    test <@ alone.LinesCovered = 3 @>
    test <@ pooled.LinesCovered = 3 @>

[<Fact>]
let ``LinesCovered rises only when hits are actually added`` () =
    // Positive control for the test above: LinesCovered is not simply frozen.
    // A pooled run that DOES add a hit must move it, or the stability claim
    // would be vacuous.
    let runA = classXml "Foo.fs" [ 1, 1; 2, 0 ]
    let runB = classXml "Foo.fs" [ 1, 0; 2, 1 ]

    let alone = parseXmls [ runA ] |> List.head
    let pooled = parseXmls [ runA; runB ] |> List.head

    test <@ alone.LinesCovered = 1 @>
    test <@ pooled.LinesCovered = 2 @>
    test <@ pooled.LinesTotal = 2 @>

[<Fact>]
let ``LinesCovered and LinesTotal agree with LinePct`` () =
    let coverage =
        parseXmls [ classXml "Foo.fs" [ 1, 1; 2, 1; 3, 0; 4, 0 ] ] |> List.head

    test <@ coverage.LinesCovered = 2 @>
    test <@ coverage.LinesTotal = 4 @>
    test <@ coverage.LinePct = 50.0 @>

// ── ReaderOptions: which sources the reader is willing to read ────────────────────

[<Fact>]
let ``parseXml - a C# report reads as zero files by default`` () =
    // The default is F#-only, and stays that way. This is the "before" half of the
    // issue's reproduction, pinned so widening the reader cannot quietly widen the
    // default with it.
    let xml = classXml "src/Handler.cs" [ 1, 1; 2, 0 ]

    test <@ parseXml xml |> List.isEmpty @>

[<Fact>]
let ``parseXmlWith - widening the extensions reads the same report`` () =
    // The issue's reproduction: identical bytes, identical numbers, different
    // extension. Renaming .cs to .fs inside the XML was enough to make the whole
    // pipeline work, which is what showed the filter was the only F#-specific thing
    // in the path.
    let asCSharp = classXml "src/Handler.cs" [ 1, 1; 2, 0 ]
    let asFSharp = classXml "src/Handler.fs" [ 1, 1; 2, 0 ]

    let options = ReaderOptions.defaults |> ReaderOptions.withExtensions [| ".cs" |]

    let widened = parseXmlWith options asCSharp
    let renamed = parseXml asFSharp

    test <@ widened.Length = 1 @>
    test <@ widened.[0].FileName = "Handler.cs" @>
    test <@ widened.[0].LinePct = 50.0 @>

    // Same coverage either way — the extension decided whether it was read, not
    // what it measured.
    test <@ widened.[0].LinePct = renamed.[0].LinePct @>
    test <@ widened.[0].LinesCovered = renamed.[0].LinesCovered @>
    test <@ widened.[0].LinesTotal = renamed.[0].LinesTotal @>

[<Fact>]
let ``parseXmlWith - several languages can be read at once`` () =
    let xmls =
        [ classXml "src/Handler.cs" [ 1, 1; 2, 0 ]
          classXml "src/Legacy.vb" [ 1, 1; 2, 1 ]
          classXml "src/Core.fs" [ 1, 0; 2, 0 ] ]

    let options =
        ReaderOptions.defaults |> ReaderOptions.withExtensions [| ".fs"; ".cs"; ".vb" |]

    let names =
        parseXmlsWith options xmls |> List.map (fun f -> f.FileName) |> List.sort

    test <@ names = [ "Core.fs"; "Handler.cs"; "Legacy.vb" ] @>

[<Fact>]
let ``parseXmlWith - widening the extensions does not disable the other filters`` () =
    // Widening says which languages to read, not which paths and names to trust.
    // A vendored C# file and a C# file whose name matches the exclusion list are
    // still dropped, exactly as their F# counterparts are.
    let xmls =
        [ classXml "src/vendor/ThirdParty.cs" [ 1, 1 ]
          classXml "src/AssemblyInfo.cs" [ 1, 1 ]
          classXml "src/Handler.cs" [ 1, 1 ] ]

    let options = ReaderOptions.defaults |> ReaderOptions.withExtensions [| ".cs" |]

    let names = parseXmlsWith options xmls |> List.map (fun f -> f.FileName)

    test <@ names = [ "Handler.cs" ] @>

[<Fact>]
let ``ReaderOptions - the exclusion lists are reachable too`` () =
    // The extension filter is what issue #2 was about, but all three lists were
    // private with no hook. A project shipping a production file the default name
    // list would drop can now say so — see issue #3 for why that came up.
    let xmls =
        [ classXml "src/TestKit.fs" [ 1, 1; 2, 0 ]
          classXml "src/Real.fs" [ 1, 1; 2, 0 ] ]

    let keepEverything =
        { ReaderOptions.defaults with
            ExcludedFileNamePatterns = [||] }

    let defaulted = parseXmls xmls |> List.map (fun f -> f.FileName)

    let widened =
        parseXmlsWith keepEverything xmls |> List.map (fun f -> f.FileName) |> List.sort

    test <@ defaulted = [ "Real.fs" ] @>
    test <@ widened = [ "Real.fs"; "TestKit.fs" ] @>

[<Fact>]
let ``extractRawLines - the parameterless form is the defaults`` () =
    // Every parameterless entry point delegates to its *With twin rather than
    // duplicating the pipeline, so this is the one assertion that keeps the two
    // from drifting apart.
    let xml = classXml "src/Core.fs" [ 1, 1; 2, 0 ]

    test <@ extractRawLines xml = extractRawLinesWith ReaderOptions.defaults xml @>
    test <@ parseXml xml = parseXmlWith ReaderOptions.defaults xml @>
    test <@ parseXmls [ xml ] = parseXmlsWith ReaderOptions.defaults [ xml ] @>

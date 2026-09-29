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

// Pooling a project that covers none of a file can enlarge its emitted-line set
// (LinesTotal) without adding hits; LinesCovered must not move.

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
    let runA = classXml "Foo.fs" [ 1, 1; 2, 1; 3, 1; 4, 0 ]

    // Same file, wider emitted set, no hits.
    let runB = classXml "Foo.fs" [ for i in 1..10 -> i, 0 ]

    let alone = parseXmls [ runA ] |> List.head
    let pooled = parseXmls [ runA; runB ] |> List.head

    test <@ alone.LinesTotal = 4 @>
    test <@ pooled.LinesTotal = 10 @>

    test <@ alone.LinePct = 75.0 @>
    test <@ pooled.LinePct = 30.0 @>

    test <@ alone.LinesCovered = 3 @>
    test <@ pooled.LinesCovered = 3 @>

[<Fact>]
let ``LinesCovered rises only when hits are actually added`` () =
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

let private csharp =
    { ReaderOptions.defaults with
        IncludedExtensions = [| ".cs" |]
    }

let private readNames (options: ReaderOptions) (xmls: string list) =
    (readReports options xmls).Lines
    |> buildCoverage
    |> List.map (fun f -> f.FileName)
    |> List.sort

let private excludedNames (report: Report) =
    report.Excluded |> List.map (fun e -> e.FileName)

[<Fact>]
let ``parseXml - a C# report reads as zero files by default`` () =
    let xml = classXml "src/Handler.cs" [ 1, 1; 2, 0 ]

    test <@ parseXml xml |> List.isEmpty @>

[<Fact>]
let ``readReports - a C# report measures the same as the F# one with the extension renamed`` () =
    let asCSharp =
        (readReports csharp [ classXml "src/Handler.cs" [ 1, 1; 2, 0 ] ]).Lines
        |> buildCoverage

    let asFSharp = parseXml (classXml "src/Handler.fs" [ 1, 1; 2, 0 ])

    test <@ asCSharp |> List.map (fun f -> f.FileName) = [ "Handler.cs" ] @>

    test
        <@
            asCSharp |> List.map (fun f -> f.LinePct, f.LinesCovered, f.LinesTotal) =
                (asFSharp |> List.map (fun f -> f.LinePct, f.LinesCovered, f.LinesTotal))
        @>

[<Fact>]
let ``readReports - several languages can be read at once`` () =
    let xmls =
        [
            classXml "src/Handler.cs" [ 1, 1; 2, 0 ]
            classXml "src/Legacy.vb" [ 1, 1; 2, 1 ]
            classXml "src/Core.fs" [ 1, 0; 2, 0 ]
        ]

    let options =
        { ReaderOptions.defaults with
            IncludedExtensions = [| ".fs"; ".cs"; ".vb" |]
        }

    test <@ readNames options xmls = [ "Core.fs"; "Handler.cs"; "Legacy.vb" ] @>

[<Fact>]
let ``readReports - widening the extensions keeps the name and path filters`` () =
    let xmls =
        [
            classXml "src/vendor/ThirdParty.cs" [ 1, 1 ]
            classXml "src/AssemblyInfo.cs" [ 1, 1 ]
            classXml "src/Handler.cs" [ 1, 1 ]
        ]

    test <@ readNames csharp xmls = [ "Handler.cs" ] @>

[<Fact>]
let ``readReports - the exclusion lists can be overridden`` () =
    let xmls =
        [
            classXml "src/TestKit.fs" [ 1, 1; 2, 0 ]
            classXml "src/Real.fs" [ 1, 1; 2, 0 ]
        ]

    let keepTestKit =
        { ReaderOptions.defaults with
            ExcludedFileNamePatterns = [||]
        }

    test <@ readNames ReaderOptions.defaults xmls = [ "Real.fs" ] @>
    test <@ readNames keepTestKit xmls = [ "Real.fs"; "TestKit.fs" ] @>

[<Fact>]
let ``extractRawLines - is readReports with the defaults`` () =
    let xml = classXml "src/Core.fs" [ 1, 1; 2, 0 ]

    test <@ extractRawLines xml = (readReports ReaderOptions.defaults [ xml ]).Lines @>

[<Fact>]
let ``readReports - names a production file caught by the name filter`` () =
    let xmls =
        [
            classXml "MyLib/Harness.fs" [ 1, 1; 2, 0 ]
            classXml "MyLib/TestKit.fs" [ 1, 1; 2, 0 ]
            classXml "MyLib/ProtestBanner.fs" [ 1, 1; 2, 0 ]
            classXml "MyLib/Latest.fs" [ 1, 1; 2, 0 ]
        ]

    let report = readReports ReaderOptions.defaults xmls

    test <@ readNames ReaderOptions.defaults xmls = [ "Harness.fs"; "Latest.fs"; "ProtestBanner.fs" ] @>

    test
        <@
            report.Excluded =
                [
                    {
                        FileName = "TestKit.fs"
                        Reason = ExcludedByFileName "Test"
                    }
                ]
        @>

[<Fact>]
let ``readReports - the name filter is case-sensitive, so Latest and ProtestBanner are read`` () =
    // Pins the current `Contains` rule; tightening it would change this test.
    let xmls =
        [
            classXml "MyLib/Latest.fs" [ 1, 1 ]
            classXml "MyLib/ProtestBanner.fs" [ 1, 1 ]
        ]

    test <@ (readReports ReaderOptions.defaults xmls).Excluded |> List.isEmpty @>

[<Fact>]
let ``readReports - reports which filter decided`` () =
    let xmls =
        [
            classXml "MyLib/Handler.cs" [ 1, 1 ]
            classXml "MyLib/AssemblyInfo.fs" [ 1, 1 ]
            classXml "MyLib/vendor/ThirdParty.fs" [ 1, 1 ]
            classXml "MyLib/Real.fs" [ 1, 1 ]
        ]

    let reasons =
        (readReports ReaderOptions.defaults xmls).Excluded
        |> List.map (fun e -> e.FileName, e.Reason)

    test
        <@
            reasons =
                [
                    "AssemblyInfo.fs", ExcludedByFileName "AssemblyInfo"
                    "Handler.cs", ExcludedByExtension ".cs"
                    "ThirdParty.fs", ExcludedByPath "vendor"
                ]
        @>

[<Fact>]
let ``readReports - every class with a filename is either read or excluded`` () =
    let xmls =
        [
            classXml "MyLib/Real.fs" [ 1, 1 ]
            classXml "MyLib/TestKit.fs" [ 1, 1 ]
            classXml "MyLib/Handler.cs" [ 1, 1 ]
            classXml "MyLib/node_modules/Dep.fs" [ 1, 1 ]
        ]

    let report = readReports ReaderOptions.defaults xmls
    let read = report.Lines |> List.map (fun l -> l.FileName) |> List.distinct

    test <@ read = [ "Real.fs" ] @>
    test <@ excludedNames report = [ "Dep.fs"; "Handler.cs"; "TestKit.fs" ] @>

[<Fact>]
let ``readReports - a clean report excludes nothing`` () =
    let xmls = [ classXml "MyLib/Real.fs" [ 1, 1 ]; classXml "MyLib/Other.fs" [ 1, 0 ] ]

    test <@ (readReports ReaderOptions.defaults xmls).Excluded |> List.isEmpty @>

[<Fact>]
let ``readReports - one exclusion per base name across several reports`` () =
    let runA = classXml "MyLib/TestKit.fs" [ 1, 1 ]
    let runB = classXml "MyLib/TestKit.fs" [ 2, 0 ]

    test <@ excludedNames (readReports ReaderOptions.defaults [ runA; runB ]) = [ "TestKit.fs" ] @>

[<Fact>]
let ``readReports - a base name read in one project and excluded in another is on both sides`` () =
    // Floors are keyed by base name, so the skipped copy is still listed.
    let xmls =
        [
            classXml "LibA/Shared.fs" [ 1, 1 ]
            classXml "LibB/vendor/Shared.fs" [ 1, 0 ]
        ]

    let report = readReports ReaderOptions.defaults xmls

    test <@ readNames ReaderOptions.defaults xmls = [ "Shared.fs" ] @>
    test <@ excludedNames report = [ "Shared.fs" ] @>

[<Fact>]
let ``ExclusionReason.describe - names the value that matched`` () =
    test <@ ExclusionReason.describe (ExcludedByExtension ".cs") = "extension \".cs\" is not read" @>
    test <@ ExclusionReason.describe (ExcludedByExtension "") = "has no extension" @>
    test <@ ExclusionReason.describe (ExcludedByFileName "Test") = "name contains \"Test\"" @>
    test <@ ExclusionReason.describe (ExcludedByPath "vendor") = "under a \"vendor\" path segment" @>

[<Fact>]
let ``readReports - with C# options an F# file is excluded by its extension`` () =
    let report =
        readReports csharp [ classXml "src/Core.fs" [ 1, 1 ]; classXml "src/Makefile" [ 1, 1 ] ]

    test <@ report.Lines |> List.isEmpty @>
    test <@ report.Excluded |> List.map (fun e -> e.Reason) = [ ExcludedByExtension ".fs"; ExcludedByExtension "" ] @>

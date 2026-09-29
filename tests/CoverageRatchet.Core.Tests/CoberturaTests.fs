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
let ``parseXml - reads F#, C# and VB sources and nothing else`` () =
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
                <class filename="/src/Baz.vb">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/build.fsx">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/Foo.fsi">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/Page.razor">
                  <lines><line number="1" hits="1" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result |> List.map (fun f -> f.FileName) |> List.sort = [ "Bar.cs"; "Baz.vb"; "Foo.fs" ] @>

[<Fact>]
let ``parseXml - excludes sources under test and obj directories`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/repo/tests/Lib.Tests/LibTests.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/repo/src/Lib/obj/Debug/net10.0/Lib.AssemblyInfo.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/repo/src/Lib/obj/Debug/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/repo/src/Lib/Real.fs">
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

let private reasons (report: Report) =
    report.Excluded |> List.map (fun e -> e.FileName, e.Reason)

[<Fact>]
let ``parseXml - a C# report is read by default`` () =
    let xml = classXml "src/Handler.cs" [ 1, 1; 2, 0 ]

    test <@ parseXml xml |> List.map (fun f -> f.FileName, f.LinesCovered, f.LinesTotal) = [ "Handler.cs", 1, 2 ] @>

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
let ``readReports - several languages are read at once by default`` () =
    let xmls =
        [
            classXml "src/Handler.cs" [ 1, 1; 2, 0 ]
            classXml "src/Legacy.vb" [ 1, 1; 2, 1 ]
            classXml "src/Core.fs" [ 1, 0; 2, 0 ]
        ]

    test <@ readNames ReaderOptions.defaults xmls = [ "Core.fs"; "Handler.cs"; "Legacy.vb" ] @>

[<Fact>]
let ``readReports - extensions match ignoring case`` () =
    let xmls = [ classXml "src/Legacy.VB" [ 1, 1 ]; classXml "src/Core.FS" [ 1, 1 ] ]

    test <@ readNames ReaderOptions.defaults xmls = [ "Core.FS"; "Legacy.VB" ] @>

[<Fact>]
let ``readReports - narrowing the extensions keeps the directory filters`` () =
    let xmls =
        [
            classXml "src/vendor/ThirdParty.cs" [ 1, 1 ]
            classXml "src/Lib/obj/Debug/net10.0/Lib.AssemblyInfo.cs" [ 1, 1 ]
            classXml "tests/Lib.Tests/HandlerTests.cs" [ 1, 1 ]
            classXml "src/Handler.cs" [ 1, 1 ]
        ]

    test <@ readNames csharp xmls = [ "Handler.cs" ] @>

[<Fact>]
let ``readReports - the directory rules can be overridden`` () =
    let xmls =
        [
            classXml "tests/Support/Fixture.fs" [ 1, 1; 2, 0 ]
            classXml "src/Real.fs" [ 1, 1; 2, 0 ]
        ]

    let readTests =
        { ReaderOptions.defaults with
            ExcludedDirectories = [||]
        }

    test <@ readNames ReaderOptions.defaults xmls = [ "Real.fs" ] @>
    test <@ readNames readTests xmls = [ "Fixture.fs"; "Real.fs" ] @>

[<Fact>]
let ``extractRawLines - is readReports with the defaults`` () =
    let xml = classXml "src/Core.fs" [ 1, 1; 2, 0 ]

    test <@ extractRawLines xml = (readReports ReaderOptions.defaults [ xml ]).Lines @>

[<Fact>]
let ``readReports - a production file with Test in its name is read`` () =
    let xmls =
        [
            classXml "src/MyLib/TestKit.fs" [ 1, 1; 2, 0 ]
            classXml "src/MyLib/Test.fs" [ 1, 1; 2, 0 ]
            classXml "src/MyLib/TestDataSeeder.fs" [ 1, 1; 2, 0 ]
            classXml "src/MyLib/AssemblyInfo.fs" [ 1, 1; 2, 0 ]
            classXml "src/MyLib/Latest.fs" [ 1, 1; 2, 0 ]
        ]

    let report = readReports ReaderOptions.defaults xmls

    test
        <@
            readNames ReaderOptions.defaults xmls =
                [ "AssemblyInfo.fs"; "Latest.fs"; "Test.fs"; "TestDataSeeder.fs"; "TestKit.fs" ]
        @>

    test <@ report.Excluded |> List.isEmpty @>

[<Fact>]
let ``readReports - test directories are matched by whole name or suffix, ignoring case`` () =
    let xmls =
        [
            classXml "/repo/tests/Lib.Tests/A.fs" [ 1, 1 ]
            classXml "/repo/Test/B.fs" [ 1, 1 ]
            classXml "/repo/src/Lib.TESTS/C.fs" [ 1, 1 ]
            classXml "/repo/src/Lib.Test/D.fs" [ 1, 1 ]
            classXml "/repo/src/Lib/OBJ/F.fs" [ 1, 1 ]
        ]

    let report = readReports ReaderOptions.defaults xmls

    test <@ report.Lines |> List.isEmpty @>

    test
        <@
            reasons report =
                [
                    "A.fs", ExcludedByDirectory(Named "tests")
                    "B.fs", ExcludedByDirectory(Named "test")
                    "C.fs", ExcludedByDirectory(NameEndsWith ".Tests")
                    "D.fs", ExcludedByDirectory(NameEndsWith ".Test")
                    "F.fs", ExcludedByDirectory(Named "obj")
                ]
        @>

[<Fact>]
let ``readReports - backslash-separated directories are matched too`` () =
    let report =
        readReports ReaderOptions.defaults [ classXml "C:\\repo\\TESTS\\Lib\\E.fs" [ 1, 1 ] ]

    test <@ report.Lines |> List.isEmpty @>
    test <@ report.Excluded |> List.map (fun e -> e.Reason) = [ ExcludedByDirectory(Named "tests") ] @>

[<Fact>]
let ``readReports - a directory that only contains the word test is read`` () =
    let xmls =
        [
            classXml "/repo/src/Latest/Feed.fs" [ 1, 1 ]
            classXml "/repo/src/Contests/Entry.fs" [ 1, 1 ]
            classXml "/repo/src/Testing/Kit.fs" [ 1, 1 ]
            classXml "/repo/src/TestsLib/Api.fs" [ 1, 1 ]
            classXml "/repo/src/objects/Model.fs" [ 1, 1 ]
        ]

    test <@ (readReports ReaderOptions.defaults xmls).Excluded |> List.isEmpty @>

[<Fact>]
let ``readReports - the file name is not a directory`` () =
    let xmls =
        [ classXml "/repo/src/tests.fs" [ 1, 1 ]; classXml "/repo/src/obj.fs" [ 1, 1 ] ]

    test <@ readNames ReaderOptions.defaults xmls = [ "obj.fs"; "tests.fs" ] @>

[<Fact>]
let ``readReports - reports which filter decided`` () =
    let xmls =
        [
            classXml "MyLib/Handler.txt" [ 1, 1 ]
            classXml "MyLib/obj/Debug/MyLib.AssemblyInfo.fs" [ 1, 1 ]
            classXml "MyLib/vendor/ThirdParty.fs" [ 1, 1 ]
            classXml "MyLib/Real.fs" [ 1, 1 ]
        ]

    test
        <@
            reasons (readReports ReaderOptions.defaults xmls) =
                [
                    "Handler.txt", ExcludedByExtension ".txt"
                    "MyLib.AssemblyInfo.fs", ExcludedByDirectory(Named "obj")
                    "ThirdParty.fs", ExcludedByDirectory(Named "vendor")
                ]
        @>

[<Fact>]
let ``readReports - every class with a filename is either read or excluded`` () =
    let xmls =
        [
            classXml "MyLib/Real.fs" [ 1, 1 ]
            classXml "MyLib/TestKit.fs" [ 1, 1 ]
            classXml "MyLib/Handler.cs" [ 1, 1 ]
            classXml "MyLib/Notes.txt" [ 1, 1 ]
            classXml "tests/MyLib.Tests/RealTests.fs" [ 1, 1 ]
            classXml "MyLib/obj/Gen.fs" [ 1, 1 ]
            classXml "MyLib/node_modules/Dep.fs" [ 1, 1 ]
        ]

    let report = readReports ReaderOptions.defaults xmls

    let read =
        report.Lines |> List.map (fun l -> l.FileName) |> List.distinct |> List.sort

    test <@ read = [ "Handler.cs"; "Real.fs"; "TestKit.fs" ] @>
    test <@ excludedNames report = [ "Dep.fs"; "Gen.fs"; "Notes.txt"; "RealTests.fs" ] @>

[<Fact>]
let ``readReports - a clean report excludes nothing`` () =
    let xmls = [ classXml "MyLib/Real.fs" [ 1, 1 ]; classXml "MyLib/Other.fs" [ 1, 0 ] ]

    test <@ (readReports ReaderOptions.defaults xmls).Excluded |> List.isEmpty @>

[<Fact>]
let ``readReports - one exclusion per base name across several reports`` () =
    let runA = classXml "tests/MyLib.Tests/Fixture.fs" [ 1, 1 ]
    let runB = classXml "tests/MyLib.Tests/Fixture.fs" [ 2, 0 ]

    test <@ excludedNames (readReports ReaderOptions.defaults [ runA; runB ]) = [ "Fixture.fs" ] @>

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
    test <@ ExclusionReason.describe (ExcludedByExtension ".txt") = "extension \".txt\" is not read" @>
    test <@ ExclusionReason.describe (ExcludedByExtension "") = "has no extension" @>
    test <@ ExclusionReason.describe (ExcludedByDirectory(Named "obj")) = "in a directory named \"obj\"" @>

    test
        <@
            ExclusionReason.describe (ExcludedByDirectory(NameEndsWith ".Tests")) =
                "in a directory whose name ends with \".Tests\""
        @>

[<Fact>]
let ``readReports - with C# options an F# file is excluded by its extension`` () =
    let report =
        readReports csharp [ classXml "src/Core.fs" [ 1, 1 ]; classXml "src/Makefile" [ 1, 1 ] ]

    test <@ report.Lines |> List.isEmpty @>
    test <@ report.Excluded |> List.map (fun e -> e.Reason) = [ ExcludedByExtension ".fs"; ExcludedByExtension "" ] @>

[<Fact>]
let ``ReaderOptions.includingOnly - narrows the defaults to the listed extensions`` () =
    let options = ReaderOptions.includingOnly [ ".fs" ]

    test
        <@
            options =
                Ok
                    { ReaderOptions.defaults with
                        IncludedExtensions = [| ".fs" |]
                    }
        @>

[<Fact>]
let ``ReaderOptions.includingOnly - reads the listed languages only`` () =
    let xmls = [ classXml "src/Core.fs" [ 1, 1 ]; classXml "src/Handler.cs" [ 1, 1 ] ]

    match ReaderOptions.includingOnly [ ".fs" ] with
    | Ok fsOnly ->
        let report = readReports fsOnly xmls
        test <@ readNames fsOnly xmls = [ "Core.fs" ] @>
        test <@ reasons report = [ "Handler.cs", ExcludedByExtension ".cs" ] @>
    | Error e -> failwith e

[<Fact>]
let ``ReaderOptions.includingOnly - matches ignoring case, in the defaults' order, once each`` () =
    let options = ReaderOptions.includingOnly [ ".CS"; ".cs"; ".Fs" ]

    test <@ options |> Result.map (fun o -> o.IncludedExtensions) = Ok [| ".fs"; ".cs" |] @>

[<Fact>]
let ``ReaderOptions.includingOnly - an empty list is an error`` () =
    test <@ ReaderOptions.includingOnly [] = Error "the extension list is empty; name one or more of .fs, .cs, .vb" @>

[<Fact>]
let ``ReaderOptions.includingOnly - an extension without its dot is an error`` () =
    test <@ ReaderOptions.includingOnly [ ".fs"; "cs" ] = Error "\"cs\" must start with \".\" (e.g. \".cs\")" @>

[<Fact>]
let ``ReaderOptions.includingOnly - an extension the reader does not know is an error`` () =
    test
        <@
            ReaderOptions.includingOnly [ ".fsx" ] =
                Error "\".fsx\" is not a source extension the reader measures; name one or more of .fs, .cs, .vb"
        @>

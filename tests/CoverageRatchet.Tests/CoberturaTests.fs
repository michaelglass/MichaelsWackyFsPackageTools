module CoverageRatchet.Tests.CoberturaTests

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
let ``parseXml - multiple classes for same file dedup lines by number`` () =
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
let ``parseXml - only fs files included`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Foo.fs">
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
                </class>
                <class filename="/src/Bar.cs">
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
                </class>
                <class filename="/src/Baz.js">
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
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
                <class filename="/src/AssemblyAttributes.fs">
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
let ``parseXml - excludes vendor paths (paket-files, vendor, node_modules, .fable)`` () =
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
                <class filename="/src/node_modules/somelib/Util.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/.fable/Fable.Core/Interop.fs">
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
let ``parseXml - excludes by filename not directory path`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/TestPrune/Analyzer.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/TestPrune/Tests/MyTest.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Analyzer.fs" @>

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
let ``parseXml - empty coverage report`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 0 @>

[<Fact>]
let ``parseXml - no classes element`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 0 @>

// --- parseFiles (multi-XML merge) ---

[<Fact>]
let ``parseFiles - merges line coverage across XMLs for same file`` () =
    let xml1 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Foo.fs">
              <lines>
                <line number="1" hits="1" />
                <line number="2" hits="0" />
                <line number="3" hits="0" />
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
                <line number="3" hits="0" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let result = parseXmls [ xml1; xml2 ]

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Foo.fs" @>
    // Lines 1 and 2 hit across XMLs, line 3 never hit => 2/3
    test <@ Math.Round(result.[0].LinePct, 1) = 66.7 @>

[<Fact>]
let ``parseFiles - merges branch coverage across XMLs taking best ratio`` () =
    let xml1 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Bar.fs">
              <lines>
                <line number="1" hits="1" condition-coverage="25% (1/4)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let xml2 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Bar.fs">
              <lines>
                <line number="1" hits="1" condition-coverage="75% (3/4)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let result = parseXmls [ xml1; xml2 ]

    test <@ result.Length = 1 @>
    test <@ result.[0].BranchesCovered = 3 @>
    test <@ result.[0].BranchesTotal = 4 @>

[<Fact>]
let ``parseFiles - different files from different XMLs both appear`` () =
    let xml1 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/A.fs">
              <lines><line number="1" hits="1" /></lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let xml2 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/B.fs">
              <lines><line number="1" hits="0" /></lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let result = parseXmls [ xml1; xml2 ]

    test <@ result.Length = 2 @>
    let names = result |> List.map (fun f -> f.FileName) |> List.sort
    test <@ names = [ "A.fs"; "B.fs" ] @>

[<Fact>]
let ``parseFiles - empty list returns empty`` () =
    let result = parseXmls []

    test <@ result = [] @>

[<Fact>]
let ``parseFiles - single XML same as parseXml`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Solo.fs">
              <lines>
                <line number="1" hits="1" />
                <line number="2" hits="0" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let single = parseXml xml
    let multi = parseXmls [ xml ]

    test <@ single.Length = multi.Length @>
    test <@ single.[0].FileName = multi.[0].FileName @>
    test <@ single.[0].LinePct = multi.[0].LinePct @>

// --- parseFiles (disk-based multi-XML merge) ---

[<Fact>]
let ``parseFiles - reads and merges multiple XML files from disk`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    let xml1Path = Path.Combine(tmpDir, "cov1.xml")
    let xml2Path = Path.Combine(tmpDir, "cov2.xml")

    File.WriteAllText(
        xml1Path,
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage><packages><package><classes>
          <class filename="/src/Merged.fs">
            <lines>
              <line number="1" hits="1" />
              <line number="2" hits="0" />
            </lines>
          </class>
        </classes></package></packages></coverage>"""
    )

    File.WriteAllText(
        xml2Path,
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage><packages><package><classes>
          <class filename="/src/Merged.fs">
            <lines>
              <line number="1" hits="0" />
              <line number="2" hits="1" />
            </lines>
          </class>
        </classes></package></packages></coverage>"""
    )

    try
        let result = parseFiles [ xml1Path; xml2Path ]

        test <@ result.Length = 1 @>
        test <@ result.[0].FileName = "Merged.fs" @>
        test <@ result.[0].LinePct = 100.0 @>
    finally
        Directory.Delete(tmpDir, true)

// --- buildBranchGaps ---

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
    // Lines 10 (1/2) and 30 (1/4) are uncovered; line 20 (2/2) is fully covered
    test <@ result.[0].Gaps.Length = 2 @>
    test <@ result.[0].Gaps.[0].Line = 10 @>
    test <@ result.[0].Gaps.[0].Covered = 1 @>
    test <@ result.[0].Gaps.[0].Total = 2 @>
    test <@ result.[0].Gaps.[1].Line = 30 @>
    test <@ result.[0].Gaps.[1].Covered = 1 @>
    test <@ result.[0].Gaps.[1].Total = 4 @>

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

    test <@ result = [] @>

[<Fact>]
let ``buildBranchGaps - file with no branches not included`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Simple.fs">
              <lines>
                <line number="1" hits="1" />
                <line number="2" hits="0" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let rawLines = extractRawLines xml
    let result = buildBranchGaps rawLines

    test <@ result = [] @>

[<Fact>]
let ``buildBranchGaps - merges branch data across XMLs`` () =
    let xml1 =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Foo.fs">
              <lines>
                <line number="5" hits="1" condition-coverage="25% (1/4)" />
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
                <line number="5" hits="1" condition-coverage="75% (3/4)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let rawLines = (extractRawLines xml1) @ (extractRawLines xml2)
    let result = buildBranchGaps rawLines

    test <@ result.Length = 1 @>
    // Should use the better ratio (3/4), still a gap since 3 < 4
    test <@ result.[0].Gaps.[0].Covered = 3 @>
    test <@ result.[0].Gaps.[0].Total = 4 @>

[<Fact>]
let ``buildBranchGaps - sorted by gap count descending`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages><package><classes>
            <class filename="/src/Few.fs">
              <lines>
                <line number="1" hits="1" condition-coverage="50% (1/2)" />
              </lines>
            </class>
            <class filename="/src/Many.fs">
              <lines>
                <line number="1" hits="1" condition-coverage="50% (1/2)" />
                <line number="2" hits="1" condition-coverage="50% (1/2)" />
                <line number="3" hits="1" condition-coverage="50% (1/2)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>"""

    let rawLines = extractRawLines xml
    let result = buildBranchGaps rawLines

    test <@ result.Length = 2 @>
    // Many.fs has 3 gaps, Few.fs has 1 — Many first
    test <@ result.[0].FileName = "Many.fs" @>
    test <@ result.[1].FileName = "Few.fs" @>

// --- findCoverageFiles (plural) ---

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
let ``findCoverageFiles - returns empty list for missing directory`` () =
    let result = findCoverageFiles "/nonexistent/path/that/does/not/exist"

    test <@ result = [] @>

[<Fact>]
let ``findCoverageFiles - returns empty list for empty directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let result = findCoverageFiles tmpDir
        test <@ result = [] @>
    finally
        Directory.Delete(tmpDir, true)

// --- findCoverageFile (singular, existing) ---

[<Fact>]
let ``findCoverageFile - discovers XML in directory`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let subDir = Path.Combine(tmpDir, "subdir")
    Directory.CreateDirectory(subDir) |> ignore

    let xmlPath = Path.Combine(subDir, "coverage.cobertura.xml")
    File.WriteAllText(xmlPath, "<coverage/>")

    try
        let result = findCoverageFile tmpDir

        test <@ result.IsSome @>
        test <@ result.Value = xmlPath @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``findCoverageFile - returns None for missing directory`` () =
    let result = findCoverageFile "/nonexistent/path/that/does/not/exist"

    test <@ result.IsNone @>

[<Fact>]
let ``parseXml - branch dedup keeps better existing ratio`` () =
    // Two classes for the same file with the same branch line number.
    // First class has 2/4 (50%) covered, second has 1/4 (25%).
    // The dedup should keep 2/4 because covered*existingT > existingC*total is false.
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="10" hits="1" condition-coverage="50% (2/4)" />
                  </lines>
                </class>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="10" hits="1" condition-coverage="25% (1/4)" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    // Should keep the better ratio (2/4 = 50%), not replace with 1/4
    test <@ result.[0].BranchesCovered = 2 @>
    test <@ result.[0].BranchesTotal = 4 @>

[<Fact>]
let ``parseXml - class element with no filename attribute is skipped`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class>
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
                </class>
                <class filename="/src/Real.fs">
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Real.fs" @>

[<Fact>]
let ``parseXml - file with zero lines gives 100 percent line coverage`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Empty.fs">
                  <lines>
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].FileName = "Empty.fs" @>
    test <@ result.[0].LinePct = 100.0 @>

[<Fact>]
let ``parseXml - line missing number or hits attribute is skipped`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Weird.fs">
                  <lines>
                    <line hits="1" />
                    <line number="2" />
                    <line number="3" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    // Only line 3 should be counted (the others are missing number or hits)
    test <@ result.[0].LinePct = 100.0 @>

[<Fact>]
let ``parseXml - branch dedup new beats existing when better coverage`` () =
    // Two classes for the same file with the same branch line number.
    // First class has 1/4 (25%) covered, second has 2/4 (50%).
    // The dedup should replace with 2/4 because second has better coverage ratio.
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="10" hits="1" condition-coverage="25% (1/4)" />
                  </lines>
                </class>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="10" hits="1" condition-coverage="50% (2/4)" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    // Should replace with the better ratio (2/4 = 50%)
    test <@ result.[0].BranchesCovered = 2 @>
    test <@ result.[0].BranchesTotal = 4 @>

[<Fact>]
let ``findCoverageFile - returns most recent when multiple files exist`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    let subDir1 = Path.Combine(tmpDir, "sub1")
    let subDir2 = Path.Combine(tmpDir, "sub2")
    Directory.CreateDirectory(subDir1) |> ignore
    Directory.CreateDirectory(subDir2) |> ignore

    let oldPath = Path.Combine(subDir1, "coverage.cobertura.xml")
    File.WriteAllText(oldPath, "<coverage/>")

    let newPath = Path.Combine(subDir2, "coverage.cobertura.xml")
    File.WriteAllText(newPath, "<coverage/>")

    // Ensure the first file has an older write time
    File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddSeconds(-10.0))

    try
        let result = findCoverageFile tmpDir

        test <@ result.IsSome @>
        test <@ result.Value = newPath @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``parseXml - condition-coverage without matching regex is ignored`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Odd.fs">
                  <lines>
                    <line number="1" hits="1" condition-coverage="unknown format" />
                    <line number="2" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].BranchesCovered = 0 @>
    test <@ result.[0].BranchesTotal = 0 @>
    test <@ result.[0].BranchPct = 100.0 @>

[<Fact>]
let ``parseXml - condition-coverage with 100 percent`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Full.fs">
                  <lines>
                    <line number="1" hits="1" condition-coverage="100% (4/4)" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].BranchesCovered = 4 @>
    test <@ result.[0].BranchesTotal = 4 @>
    test <@ result.[0].BranchPct = 100.0 @>

[<Fact>]
let ``parseXml - namespaced XML`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage xmlns="http://example.com/coverage">
          <packages>
            <package>
              <classes>
                <class filename="/src/Ns.fs">
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
    test <@ result.[0].FileName = "Ns.fs" @>
    test <@ result.[0].LinePct = 50.0 @>

[<Fact>]
let ``parseXml - branch dedup keeps existing when ratios are equal`` () =
    // Both classes have identical branch ratios (1/2). The second should NOT replace.
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Eq.fs">
                  <lines>
                    <line number="5" hits="1" condition-coverage="50% (1/2)" />
                  </lines>
                </class>
                <class filename="/src/Eq.fs">
                  <lines>
                    <line number="5" hits="1" condition-coverage="50% (1/2)" />
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

// --- parseFile reads from disk ---

[<Fact>]
let ``parseFile - reads and parses XML from file path`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    let xmlPath = Path.Combine(tmpDir, "coverage.cobertura.xml")

    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Module.fs">
                  <lines>
                    <line number="1" hits="1" />
                    <line number="2" hits="0" />
                    <line number="3" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    File.WriteAllText(xmlPath, xml)

    try
        let result = parseFile xmlPath

        test <@ result.Length = 1 @>
        test <@ result.[0].FileName = "Module.fs" @>
        test <@ Math.Round(result.[0].LinePct, 1) = 66.7 @>
    finally
        Directory.Delete(tmpDir, true)

// --- findCoverageFile returns None for empty directory ---

[<Fact>]
let ``findCoverageFile - empty directory returns None`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let result = findCoverageFile tmpDir
        test <@ result.IsNone @>
    finally
        Directory.Delete(tmpDir, true)

// --- parseXml with multiple lines all hit ---

[<Fact>]
let ``parseXml - all lines hit gives 100 percent`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Full.fs">
                  <lines>
                    <line number="1" hits="3" />
                    <line number="2" hits="1" />
                    <line number="3" hits="10" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].LinePct = 100.0 @>

// --- parseXml with multiple branches on different lines ---

[<Fact>]
let ``parseXml - multiple branch lines aggregate correctly`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Multi.fs">
                  <lines>
                    <line number="1" hits="1" condition-coverage="100% (2/2)" />
                    <line number="2" hits="1" condition-coverage="50% (1/2)" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    // 2+1 = 3 covered, 2+2 = 4 total
    test <@ result.[0].BranchesCovered = 3 @>
    test <@ result.[0].BranchesTotal = 4 @>
    test <@ result.[0].BranchPct = 75.0 @>

// --- parseXml excludes path-insensitive vendor dirs ---

[<Fact>]
let ``parseXml - excludes vendor path case insensitively`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Vendor/ThirdParty.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/NODE_MODULES/Util.fs">
                  <lines><line number="1" hits="1" /></lines>
                </class>
                <class filename="/src/Paket-Files/Lib.fs">
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

// --- parseXml line dedup: first miss then hit results in hit ---

[<Fact>]
let ``parseXml - line dedup first miss then hit counts as hit`` () =
    let xml =
        """<?xml version="1.0" encoding="utf-8"?>
        <coverage>
          <packages>
            <package>
              <classes>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="1" hits="0" />
                  </lines>
                </class>
                <class filename="/src/Dup.fs">
                  <lines>
                    <line number="1" hits="1" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>"""

    let result = parseXml xml

    test <@ result.Length = 1 @>
    test <@ result.[0].LinePct = 100.0 @>

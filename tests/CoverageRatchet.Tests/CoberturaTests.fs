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

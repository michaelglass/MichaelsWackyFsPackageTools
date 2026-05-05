module CoverageRatchet.Core.Tests.MergeTests

open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Merge

let private fullCoverageXml =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1" branch-rate="1" version="1.9" timestamp="1000">
  <packages>
    <package name="src" line-rate="1" branch-rate="1" complexity="0">
      <classes>
        <class name="Foo" filename="src/Foo.fs" line-rate="1" branch-rate="1" complexity="0">
          <lines>
            <line number="1" hits="2" branch="false" />
            <line number="2" hits="1" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

let private partialCoverageXml =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.5" branch-rate="1" version="1.9" timestamp="2000">
  <packages>
    <package name="src" line-rate="0.5" branch-rate="1" complexity="0">
      <classes>
        <class name="Foo" filename="src/Foo.fs" line-rate="0.5" branch-rate="1" complexity="0">
          <lines>
            <line number="1" hits="0" branch="false" />
            <line number="2" hits="3" branch="false" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

[<Fact>]
let ``mergeFiles - takes max hit counts per line`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    let baselinePath = Path.Combine(tmpDir, "baseline.xml")
    let partialPath = Path.Combine(tmpDir, "partial.xml")
    let outputPath = Path.Combine(tmpDir, "output.xml")

    File.WriteAllText(baselinePath, fullCoverageXml)
    File.WriteAllText(partialPath, partialCoverageXml)

    try
        mergeFiles baselinePath partialPath outputPath

        let merged = System.Xml.Linq.XDocument.Load(outputPath)
        let lines = merged.Descendants(System.Xml.Linq.XName.Get("line")) |> Seq.toList

        // Line 1: max(2, 0) = 2; Line 2: max(1, 3) = 3
        let line1 =
            lines
            |> List.find (fun l -> l.Attribute(System.Xml.Linq.XName.Get("number")).Value = "1")

        let line2 =
            lines
            |> List.find (fun l -> l.Attribute(System.Xml.Linq.XName.Get("number")).Value = "2")

        test <@ line1.Attribute(System.Xml.Linq.XName.Get("hits")).Value = "2" @>

        test <@ line2.Attribute(System.Xml.Linq.XName.Get("hits")).Value = "3" @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``mergeFiles - uses partial as baseline when baseline missing`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    Directory.CreateDirectory(tmpDir) |> ignore

    let baselinePath = Path.Combine(tmpDir, "missing_baseline.xml")
    let partialPath = Path.Combine(tmpDir, "partial.xml")
    let outputPath = Path.Combine(tmpDir, "output.xml")

    File.WriteAllText(partialPath, partialCoverageXml)

    try
        mergeFiles baselinePath partialPath outputPath

        test <@ File.Exists(outputPath) @>
        let content = File.ReadAllText(outputPath)
        test <@ content.Contains("Foo.fs") || content.Contains("src/Foo.fs") @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``mergeIntoBaselines - bootstraps baseline from first run`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    let projDir = Path.Combine(tmpDir, "Project")
    Directory.CreateDirectory(projDir) |> ignore

    let coverageFile = Path.Combine(projDir, "coverage.cobertura.xml")
    File.WriteAllText(coverageFile, fullCoverageXml)

    try
        mergeIntoBaselines tmpDir

        let baselinePath = Path.Combine(projDir, "coverage.baseline.xml")
        test <@ File.Exists(baselinePath) @>
    finally
        Directory.Delete(tmpDir, true)

[<Fact>]
let ``refreshBaselines - copies cobertura to baseline`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString())
    let projDir = Path.Combine(tmpDir, "Project")
    Directory.CreateDirectory(projDir) |> ignore

    let coverageFile = Path.Combine(projDir, "coverage.cobertura.xml")
    File.WriteAllText(coverageFile, fullCoverageXml)

    // Create an old baseline
    let baselinePath = Path.Combine(projDir, "coverage.baseline.xml")
    File.WriteAllText(baselinePath, "<coverage/>")

    try
        refreshBaselines tmpDir

        let refreshedContent = File.ReadAllText(baselinePath)
        test <@ refreshedContent.Contains("line-rate") @>
    finally
        Directory.Delete(tmpDir, true)

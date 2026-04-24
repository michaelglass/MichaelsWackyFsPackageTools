module CoverageRatchet.Tests.MergeTests

open System
open System.IO
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open CoverageRatchet.Merge

// ---------- Helpers ----------

let private xn (s: string) = XName.Get s

/// Build a minimal Cobertura document. `lines` is a list per class:
/// (packageName, filename, className, lines=[(num, hits, branchCC option)])
type LineSpec =
    { Num: int
      Hits: int
      Branch: (int * int) option } // condition-coverage covered/total

type ClassSpec =
    { Package: string
      Filename: string
      ClassName: string
      Lines: LineSpec list }

let line n h = { Num = n; Hits = h; Branch = None }

let branchLine n h c t =
    { Num = n
      Hits = h
      Branch = Some(c, t) }

let buildCobertura (classes: ClassSpec list) : string =
    let groupedByPkg = classes |> List.groupBy (fun c -> c.Package)

    let packagesXml =
        groupedByPkg
        |> List.map (fun (pkgName, cs) ->
            let classesXml =
                cs
                |> List.map (fun c ->
                    let linesXml =
                        c.Lines
                        |> List.map (fun l ->
                            match l.Branch with
                            | Some(cov, tot) ->
                                sprintf
                                    "<line number=\"%d\" hits=\"%d\" branch=\"true\" condition-coverage=\"%d%% (%d/%d)\"><conditions><condition number=\"0\" type=\"jump\" coverage=\"%d%%\" /></conditions></line>"
                                    l.Num
                                    l.Hits
                                    (if tot = 0 then 100 else 100 * cov / tot)
                                    cov
                                    tot
                                    (if tot = 0 then 100 else 100 * cov / tot)
                            | None -> sprintf "<line number=\"%d\" hits=\"%d\" branch=\"false\" />" l.Num l.Hits)
                        |> String.concat ""

                    sprintf
                        "<class name=\"%s\" filename=\"%s\" line-rate=\"0\" branch-rate=\"0\"><methods /><lines>%s</lines></class>"
                        c.ClassName
                        c.Filename
                        linesXml)
                |> String.concat ""

            sprintf
                "<package name=\"%s\" line-rate=\"0\" branch-rate=\"0\"><classes>%s</classes></package>"
                pkgName
                classesXml)
        |> String.concat ""

    sprintf
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><coverage line-rate=\"0\" branch-rate=\"0\" lines-covered=\"0\" lines-valid=\"0\" branches-covered=\"0\" branches-valid=\"0\" version=\"1\" timestamp=\"0\"><sources><source>.</source></sources><packages>%s</packages></coverage>"
        packagesXml

let writeTempCobertura (content: string) : string =
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "cov-%s.xml" (Guid.NewGuid().ToString("N")))

    File.WriteAllText(path, content)
    path

let withTempDir (f: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), sprintf "merge-test-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory(dir) |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let loadRoot (path: string) = XDocument.Load(path: string).Root

/// Extract a flat set of (filename, line-number, hits, branch) from a document.
let extractLines (root: XElement) =
    root.Descendants(xn "class")
    |> Seq.collect (fun c ->
        let fn = c.Attribute(xn "filename") |> (fun a -> if isNull a then "" else a.Value)

        c.Descendants(xn "line")
        |> Seq.map (fun l ->
            let n = (l.Attribute(xn "number")).Value |> int
            let h = (l.Attribute(xn "hits")).Value |> int

            let br =
                let a = l.Attribute(xn "branch")
                not (isNull a) && a.Value.ToLowerInvariant() = "true"

            (fn, n, h, br)))
    |> Seq.toList

let findLine (root: XElement) (filename: string) (num: int) =
    extractLines root
    |> List.tryFind (fun (fn, n, _, _) -> fn = filename && n = num)

let attrInt (root: XElement) name = root.Attribute(xn name).Value |> int

let attrFloat (root: XElement) (name: string) =
    Double.Parse(root.Attribute(xn name).Value, System.Globalization.CultureInfo.InvariantCulture)

// ---------- Tests ----------

[<Fact>]
let ``cold start: mergeIntoBaselines bootstraps baseline from coverage`` () =
    withTempDir (fun dir ->
        let sub = Path.Combine(dir, "TestProj")
        Directory.CreateDirectory(sub) |> ignore

        let coverage = Path.Combine(sub, "coverage.cobertura.xml")
        let baseline = Path.Combine(sub, "coverage.baseline.xml")

        let xml =
            buildCobertura
                [ { Package = "pkg"
                    Filename = "Foo.fs"
                    ClassName = "Foo"
                    Lines = [ line 1 5; line 2 0 ] } ]

        File.WriteAllText(coverage, xml)
        test <@ not (File.Exists baseline) @>

        mergeIntoBaselines dir

        test <@ File.Exists baseline @>
        // coverage.cobertura.xml is unchanged (since no prior baseline existed).
        test <@ File.ReadAllText(coverage) = xml @>

        // baseline byte-copy of coverage
        test <@ File.ReadAllBytes(baseline) = File.ReadAllBytes(coverage) @>)

[<Fact>]
let ``warm merge preserves baseline hits when partial drops to zero`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "baseline.xml")
        let partialPath = Path.Combine(dir, "partial.xml")
        let outPath = Path.Combine(dir, "out.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 10 10; line 20 3 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 10 0; line 20 0 ] } ]
        )

        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        test <@ findLine root "F.fs" 10 = Some("F.fs", 10, 10, false) @>
        test <@ findLine root "F.fs" 20 = Some("F.fs", 20, 3, false) @>)

[<Fact>]
let ``warm merge raises to partial hits when partial has new coverage`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 0; line 2 0 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 5; line 2 7 ] } ]
        )

        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        test <@ findLine root "F.fs" 1 = Some("F.fs", 1, 5, false) @>
        test <@ findLine root "F.fs" 2 = Some("F.fs", 2, 7, false) @>)

[<Theory>]
[<InlineData(7, 3)>]
[<InlineData(0, 10)>]
[<InlineData(5, 5)>]
let ``merge is commutative on line hits`` (aHits: int) (bHits: int) =
    withTempDir (fun dir ->
        let aPath = Path.Combine(dir, "a.xml")
        let bPath = Path.Combine(dir, "b.xml")
        let abOut = Path.Combine(dir, "ab.xml")
        let baOut = Path.Combine(dir, "ba.xml")

        File.WriteAllText(
            aPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 aHits; line 2 aHits ] } ]
        )

        File.WriteAllText(
            bPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 bHits; line 2 bHits ] } ]
        )

        mergeFiles aPath bPath abOut
        mergeFiles bPath aPath baOut

        let ab = extractLines (loadRoot abOut) |> List.sort
        let ba = extractLines (loadRoot baOut) |> List.sort
        test <@ ab = ba @>)

[<Fact>]
let ``monotonic: merged hits >= baseline hits for every baseline line`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Moderately sized synthetic fixture: 20 files x 25 lines each.
        let baselineClasses =
            [ for i in 1..20 ->
                  { Package = sprintf "pkg%d" (i % 3)
                    Filename = sprintf "File%d.fs" i
                    ClassName = sprintf "File%d" i
                    Lines = [ for ln in 1..25 -> line ln ((ln * i) % 9) ] } ]

        let partialClasses =
            [ for i in 1..20 ->
                  { Package = sprintf "pkg%d" (i % 3)
                    Filename = sprintf "File%d.fs" i
                    ClassName = sprintf "File%d" i
                    Lines = [ for ln in 1..25 -> line ln ((ln + i) % 7) ] } ]

        File.WriteAllText(baselinePath, buildCobertura baselineClasses)
        File.WriteAllText(partialPath, buildCobertura partialClasses)

        mergeFiles baselinePath partialPath outPath
        let baselineLines = extractLines (loadRoot baselinePath)

        let mergedMap =
            extractLines (loadRoot outPath)
            |> List.map (fun (fn, n, h, b) -> ((fn, n), (h, b)))
            |> Map.ofList

        for (fn, n, bh, _) in baselineLines do
            let mh, _ = mergedMap.[(fn, n)]
            test <@ mh >= bh @>)

[<Fact>]
let ``new file in partial appears in merged output`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "Old.fs"
                    ClassName = "Old"
                    Lines = [ line 1 1 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "Old.fs"
                    ClassName = "Old"
                    Lines = [ line 1 1 ] }
                  { Package = "p"
                    Filename = "Brand/New.fs"
                    ClassName = "New"
                    Lines = [ line 1 4; line 2 2 ] } ]
        )

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath

        let files =
            extractLines root
            |> List.map (fun (f, _, _, _) -> f)
            |> List.distinct
            |> List.sort

        test <@ files = [ "Brand/New.fs"; "Old.fs" ] @>
        test <@ findLine root "Brand/New.fs" 1 = Some("Brand/New.fs", 1, 4, false) @>)

[<Fact>]
let ``file in baseline but absent from partial persists in merged output`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "Keep.fs"
                    ClassName = "Keep"
                    Lines = [ line 1 3; line 2 4 ] }
                  { Package = "p"
                    Filename = "Also.fs"
                    ClassName = "Also"
                    Lines = [ line 1 7 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "Also.fs"
                    ClassName = "Also"
                    Lines = [ line 1 0 ] } ]
        )

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        test <@ findLine root "Keep.fs" 1 = Some("Keep.fs", 1, 3, false) @>
        test <@ findLine root "Keep.fs" 2 = Some("Keep.fs", 2, 4, false) @>
        test <@ findLine root "Also.fs" 1 = Some("Also.fs", 1, 7, false) @>)

[<Fact>]
let ``rate recomputation is consistent with merged line counts`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "A.fs"
                    ClassName = "A"
                    Lines = [ line 1 1; line 2 0; line 3 0 ] }
                  { Package = "p"
                    Filename = "B.fs"
                    ClassName = "B"
                    Lines = [ line 1 1; line 2 1 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "p"
                    Filename = "A.fs"
                    ClassName = "A"
                    Lines = [ line 2 1 ] } ]
        )

        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        let lines = extractLines root
        // Distinct (file,line) count — methods have no lines in our fixture so classLines dedupes cleanly.
        let distinct = lines |> List.distinctBy (fun (f, n, _, _) -> (f, n))
        let total = distinct.Length
        let covered = distinct |> List.filter (fun (_, _, h, _) -> h > 0) |> List.length

        test <@ attrInt root "lines-valid" = total @>
        test <@ attrInt root "lines-covered" = covered @>

        let expectedRate = float covered / float total
        let actualRate = attrFloat root "line-rate"
        test <@ abs (actualRate - expectedRate) < 0.001 @>)

[<Fact>]
let ``refreshBaselines overwrites baselines with current coverage`` () =
    withTempDir (fun dir ->
        let sub = Path.Combine(dir, "P1")
        Directory.CreateDirectory(sub) |> ignore

        let coverage = Path.Combine(sub, "coverage.cobertura.xml")
        let baseline = Path.Combine(sub, "coverage.baseline.xml")

        let old =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 0 ] } ]

        let fresh =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 99 ] } ]

        File.WriteAllText(baseline, old)
        File.WriteAllText(coverage, fresh)

        refreshBaselines dir

        test <@ File.ReadAllBytes(baseline) = File.ReadAllBytes(coverage) @>
        test <@ File.ReadAllText(baseline) = fresh @>)

[<Fact>]
let ``malformed input raises without corrupting baseline`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        let originalBaseline =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 5 ] } ]

        File.WriteAllText(baselinePath, originalBaseline)
        File.WriteAllText(partialPath, "this is not <valid xml")

        raises<System.Xml.XmlException> <@ mergeFiles baselinePath partialPath outPath @>

        // baseline on disk was not modified (merge writes to outPath, not baseline)
        test <@ File.ReadAllText(baselinePath) = originalBaseline @>)

[<Fact>]
let ``idempotency: merge(baseline, baseline) is semantically equal to baseline`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let outPath = Path.Combine(dir, "o.xml")

        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 1; line 2 0; line 3 4 ] }
                  { Package = "q"
                    Filename = "G.fs"
                    ClassName = "G"
                    Lines = [ line 10 2 ] } ]
        )

        mergeFiles baselinePath baselinePath outPath

        let before = extractLines (loadRoot baselinePath) |> List.sort
        let after = extractLines (loadRoot outPath) |> List.sort
        test <@ before = after @>)

// ---------- Follow-up coverage tests ----------

[<Fact>]
let ``mergeIntoBaselines raises on a corrupt baseline`` () =
    withTempDir (fun dir ->
        // Two projects; one has a garbled baseline, the other is healthy.
        let p1 = Path.Combine(dir, "BadProj")
        let p2 = Path.Combine(dir, "GoodProj")
        Directory.CreateDirectory(p1) |> ignore
        Directory.CreateDirectory(p2) |> ignore

        let healthy =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 1 ] } ]

        File.WriteAllText(Path.Combine(p1, "coverage.cobertura.xml"), healthy)
        File.WriteAllText(Path.Combine(p1, "coverage.baseline.xml"), "not xml at all")
        File.WriteAllText(Path.Combine(p2, "coverage.cobertura.xml"), healthy)
        File.WriteAllText(Path.Combine(p2, "coverage.baseline.xml"), healthy)

        // The failure must identify which project's baseline is bad; otherwise
        // a user seeing a CI red has to go grep the tree. Expect the exception
        // message to mention the project dir (or its coverage.baseline.xml
        // path), and the inner exception to retain the original XmlException
        // for stack-trace diagnosis.
        let ex = Assert.ThrowsAny(fun () -> mergeIntoBaselines dir)
        test <@ ex.Message.Contains("BadProj") @>
        test <@ ex.InnerException :? System.Xml.XmlException @>)

[<Fact>]
let ``mergeFiles handles baseline with UTF-8 BOM`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        let baselineXml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 2; line 2 0 ] } ]

        // Write baseline with a UTF-8 BOM (0xEF 0xBB 0xBF) prefix.
        let bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]
        let bodyBytes = System.Text.Encoding.UTF8.GetBytes(baselineXml)
        File.WriteAllBytes(baselinePath, Array.append bom bodyBytes)

        // Partial (no BOM) raises hits on line 1 and introduces line 2 miss.
        let partialXml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 9; line 2 0 ] } ]

        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath

        // Output parses cleanly via XDocument.
        let root = loadRoot outPath
        // max-hits semantics preserved across the BOM'd input.
        test <@ findLine root "F.fs" 1 = Some("F.fs", 1, 9, false) @>
        test <@ findLine root "F.fs" 2 = Some("F.fs", 2, 0, false) @>

        // Declared encoding is sensible UTF-8 (case-insensitive).
        let decl = XDocument.Load(outPath: string).Declaration
        test <@ not (isNull decl) @>
        test <@ decl.Encoding.ToLowerInvariant().StartsWith("utf") @>)

[<Fact>]
let ``baseline-only class in same package persists after merge`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline has Foo.fs in package P.
        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "P"
                    Filename = "Foo.fs"
                    ClassName = "Foo"
                    Lines = [ line 1 3 ] } ]
        )

        // Partial has a DIFFERENT class Bar.fs in the SAME package P.
        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "P"
                    Filename = "Bar.fs"
                    ClassName = "Bar"
                    Lines = [ line 1 7 ] } ]
        )

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath

        let files =
            extractLines root
            |> List.map (fun (f, _, _, _) -> f)
            |> List.distinct
            |> List.sort
        // Both classes must survive.
        test <@ files = [ "Bar.fs"; "Foo.fs" ] @>
        test <@ findLine root "Foo.fs" 1 = Some("Foo.fs", 1, 3, false) @>
        test <@ findLine root "Bar.fs" 1 = Some("Bar.fs", 1, 7, false) @>)

[<Fact>]
let ``baseline-only method in same class persists after merge`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline: class C has two methods a() and b().
        let baselineXml =
            """<?xml version="1.0" encoding="utf-8"?><coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0" version="1" timestamp="0"><sources><source>.</source></sources><packages><package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="1" hits="1" branch="false" /></lines></method><method name="b" signature="()"><lines><line number="10" hits="2" branch="false" /></lines></method></methods><lines><line number="1" hits="1" branch="false" /><line number="10" hits="2" branch="false" /></lines></class></classes></package></packages></coverage>"""

        // Partial: class C has only method a() (b() is absent).
        let partialXml =
            """<?xml version="1.0" encoding="utf-8"?><coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0" version="1" timestamp="0"><sources><source>.</source></sources><packages><package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="1" hits="3" branch="false" /></lines></method></methods><lines><line number="1" hits="3" branch="false" /></lines></class></classes></package></packages></coverage>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath

        let methodNames =
            root.Descendants(xn "method")
            |> Seq.map (fun m -> m.Attribute(xn "name").Value)
            |> Seq.toList
            |> List.sort

        test <@ methodNames = [ "a"; "b" ] @>)

[<Fact>]
let ``empty partial (no packages) leaves baseline data intact`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        let baselineXml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 4; line 2 1 ] } ]

        File.WriteAllText(baselinePath, baselineXml)

        // Partial has <coverage> root with an empty <packages/> — no data to merge.
        let emptyPartial =
            """<?xml version="1.0" encoding="utf-8"?><coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0" version="1" timestamp="0"><sources><source>.</source></sources><packages /></coverage>"""

        File.WriteAllText(partialPath, emptyPartial)

        mergeFiles baselinePath partialPath outPath

        // Baseline data is fully preserved.
        let root = loadRoot outPath
        test <@ findLine root "F.fs" 1 = Some("F.fs", 1, 4, false) @>
        test <@ findLine root "F.fs" 2 = Some("F.fs", 2, 1, false) @>)

[<Fact>]
let ``mergeIntoBaselines skips baseline-only project (no cobertura.xml)`` () =
    withTempDir (fun dir ->
        // One project dir contains only a baseline, no coverage.cobertura.xml —
        // findCoverageFiles iterates by coverage.cobertura.xml presence, so this
        // project is simply skipped.
        let orphan = Path.Combine(dir, "Orphan")
        Directory.CreateDirectory(orphan) |> ignore

        let baselineXml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 42 ] } ]

        let baselinePath = Path.Combine(orphan, "coverage.baseline.xml")
        File.WriteAllText(baselinePath, baselineXml)
        let baselineBefore = File.ReadAllBytes(baselinePath)

        // Must not throw; baseline must be untouched.
        mergeIntoBaselines dir

        let baselineAfter = File.ReadAllBytes(baselinePath)
        test <@ baselineBefore = baselineAfter @>)

// ---------- Branch-line merging coverage ----------

/// Helper: build a cobertura doc from a list of already-built <package> XML strings.
let private buildWithPackages (packagesXml: string) =
    sprintf
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><coverage line-rate=\"0\" branch-rate=\"0\" lines-covered=\"0\" lines-valid=\"0\" branches-covered=\"0\" branches-valid=\"0\" version=\"1\" timestamp=\"0\"><sources><source>.</source></sources><packages>%s</packages></coverage>"
        packagesXml

[<Fact>]
let ``branch line merges conditions and picks higher covered for condition-coverage`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline: line 5 is a branch line with 1/4 covered, condition 0 at 25%.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="5" hits="3" branch="true" condition-coverage="25% (1/4)"><conditions><condition number="0" type="jump" coverage="25%" /></conditions></line></lines></class></classes></package>"""

        // Partial: line 5 is a branch line with 3/4 covered, condition 0 at 75%,
        // and introduces a NEW condition 1 at 50%.
        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="5" hits="7" branch="true" condition-coverage="75% (3/4)"><conditions><condition number="0" type="jump" coverage="75%" /><condition number="1" type="jump" coverage="50%" /></conditions></line></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        // Max hits wins.
        test <@ mergedLine.Attribute(xn "hits").Value = "7" @>
        test <@ mergedLine.Attribute(xn "branch").Value = "true" @>
        // condition-coverage picks the side with more covered (partial: 3 > 1).
        test <@ mergedLine.Attribute(xn "condition-coverage").Value.Contains("3/4") @>

        // Conditions are unioned (0 and 1 present), sorted by number.
        let conds =
            mergedLine.Descendants(xn "condition")
            |> Seq.map (fun c -> c.Attribute(xn "number").Value, c.Attribute(xn "coverage").Value)
            |> Seq.toList

        test <@ conds = [ ("0", "75%"); ("1", "50%") ] @>)

[<Fact>]
let ``branch line picks baseline condition-coverage when it has strictly more covered`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline 3/4 > partial 1/4: baseline wins.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="5" hits="10" branch="true" condition-coverage="75% (3/4)"><conditions><condition number="0" type="jump" coverage="75%" /></conditions></line></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="5" hits="2" branch="true" condition-coverage="25% (1/4)"><conditions><condition number="0" type="jump" coverage="25%" /></conditions></line></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        test <@ mergedLine.Attribute(xn "condition-coverage").Value.Contains("3/4") @>
        // Baseline condition (75%) is preserved since it exceeds partial's 25%.
        let cond = mergedLine.Descendants(xn "condition") |> Seq.head
        test <@ cond.Attribute(xn "coverage").Value = "75%" @>)

[<Fact>]
let ``branch line tie on covered picks max total for condition-coverage`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // baseline 2/4, partial 2/6 — tied on covered, partial has more total.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (2/4)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="33.33% (2/6)"><conditions><condition number="0" type="jump" coverage="33%" /></conditions></line></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        // Max total wins on tie: 2/6.
        test <@ mergedLine.Attribute(xn "condition-coverage").Value.Contains("2/6") @>)

[<Fact>]
let ``branch line where only one side has condition-coverage keeps that side`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline has condition-coverage; partial's line declares branch="true"
        // but has no condition-coverage attribute.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="2" branch="true" /></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        test <@ mergedLine.Attribute(xn "condition-coverage").Value.Contains("1/2") @>)

[<Fact>]
let ``branch line with malformed condition-coverage (no parens) yields no summary`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // condition-coverage string is missing "(x/y)" — parseCondCoverage -> None.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="weird" /></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="2" branch="true" condition-coverage="also-weird" /></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        // hits still merged to max; condition-coverage was unparseable so attr is left as-is (one of the inputs).
        test <@ mergedLine.Attribute(xn "hits").Value = "2" @>
        test <@ mergedLine.Attribute(xn "branch").Value = "true" @>)

[<Fact>]
let ``branch line with condition-coverage denominator zero is treated as unparseable`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // total=0 fails the `b > 0` guard in parseCondCoverage -> None.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="0% (0/0)" /></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, baselineXml)
        mergeFiles baselinePath partialPath outPath

        // Just assert it doesn't throw and produces a valid doc.
        let root = loadRoot outPath
        test <@ root.Descendants(xn "line") |> Seq.length = 1 @>)

[<Fact>]
let ``branch line where baseline non-branch and partial branch promotes to branch`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // baseline line is branch="false"; partial promotes same line to branch="true".
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="false" /></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath
        let mergedLine = root.Descendants(xn "line") |> Seq.head
        test <@ mergedLine.Attribute(xn "branch").Value = "true" @>
        test <@ mergedLine.Attribute(xn "condition-coverage").Value.Contains("1/2") @>
        test <@ mergedLine.Descendants(xn "condition") |> Seq.length = 1 @>)

[<Fact>]
let ``method-level lines are merged when methods match by name+signature`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Both sides have matching method a(); method-level lines differ — we
        // verify they merge (max hits) inside the <method><lines> element.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="1" hits="1" branch="false" /><line number="2" hits="0" branch="false" /></lines></method></methods></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="2" hits="9" branch="false" /><line number="3" hits="5" branch="false" /></lines></method></methods></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath

        let methLines =
            root.Descendants(xn "method")
            |> Seq.filter (fun m -> m.Attribute(xn "name").Value = "a")
            |> Seq.collect (fun m -> m.Descendants(xn "line"))
            |> Seq.map (fun l -> l.Attribute(xn "number").Value |> int, l.Attribute(xn "hits").Value |> int)
            |> Seq.sortBy fst
            |> Seq.toList

        test <@ methLines = [ (1, 1); (2, 9); (3, 5) ] @>)

[<Fact>]
let ``class in partial with methods but none in baseline creates methods element`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline class C has only <lines>; partial class C has <methods>.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="false" /></lines></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="1" hits="2" branch="false" /></lines></method></methods></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        // The merged class must now contain a <methods> element with method "a".
        let methods =
            root.Descendants(xn "class")
            |> Seq.filter (fun c -> c.Attribute(xn "filename").Value = "C.fs")
            |> Seq.collect (fun c -> c.Descendants(xn "method"))
            |> Seq.map (fun m -> m.Attribute(xn "name").Value)
            |> Seq.toList

        test <@ methods = [ "a" ] @>)

[<Fact>]
let ``method with lines in partial but not baseline adds lines element`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Matching method a() — baseline has method a() with NO <lines>, partial has <lines>.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"></method></methods></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="()"><lines><line number="1" hits="7" branch="false" /></lines></method></methods></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath

        let lineHits =
            root.Descendants(xn "method")
            |> Seq.filter (fun m -> m.Attribute(xn "name").Value = "a")
            |> Seq.collect (fun m -> m.Descendants(xn "line"))
            |> Seq.map (fun l -> l.Attribute(xn "hits").Value |> int)
            |> Seq.toList

        test <@ lineHits = [ 7 ] @>)

[<Fact>]
let ``package in partial but not baseline is copied wholesale`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Baseline has package "existing"; partial introduces package "brandNew".
        File.WriteAllText(
            baselinePath,
            buildCobertura
                [ { Package = "existing"
                    Filename = "A.fs"
                    ClassName = "A"
                    Lines = [ line 1 1 ] } ]
        )

        File.WriteAllText(
            partialPath,
            buildCobertura
                [ { Package = "brandNew"
                    Filename = "B.fs"
                    ClassName = "B"
                    Lines = [ line 1 4 ] } ]
        )

        mergeFiles baselinePath partialPath outPath
        let root = loadRoot outPath

        let packageNames =
            root.Descendants(xn "package")
            |> Seq.map (fun p -> p.Attribute(xn "name").Value)
            |> Seq.toList
            |> List.sort

        test <@ packageNames = [ "brandNew"; "existing" ] @>
        // brandNew package data made it through.
        test <@ findLine root "B.fs" 1 = Some("B.fs", 1, 4, false) @>)

[<Fact>]
let ``baseline package missing classes element gains one from partial`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // baseline has package p with NO <classes> element; partial provides one.
        let baselineXml =
            buildWithPackages """<package name="p" line-rate="0" branch-rate="0" />"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="A" filename="A.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="2" branch="false" /></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        test <@ findLine root "A.fs" 1 = Some("A.fs", 1, 2, false) @>)

[<Fact>]
let ``baseline missing packages element gains one from partial`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // baseline has <coverage> with NO <packages> element at all.
        let baselineXml =
            """<?xml version="1.0" encoding="utf-8"?><coverage line-rate="0" branch-rate="0" lines-covered="0" lines-valid="0" branches-covered="0" branches-valid="0" version="1" timestamp="0"><sources><source>.</source></sources></coverage>"""

        let partialXml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 3 ] } ]

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        test <@ findLine root "F.fs" 1 = Some("F.fs", 1, 3, false) @>)

[<Fact>]
let ``recomputeRates counts branches from condition-coverage on branch lines`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // One branch line (2/4) + one non-branch line (hit) + one non-branch line (missed).
        let xml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><lines><line number="1" hits="1" branch="false" /><line number="2" hits="0" branch="false" /><line number="3" hits="4" branch="true" condition-coverage="50% (2/4)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line></lines></class></classes></package>"""

        File.WriteAllText(baselinePath, xml)
        File.WriteAllText(partialPath, xml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        // 3 lines, 2 covered. 1 branch line with 2/4 covered.
        test <@ attrInt root "lines-valid" = 3 @>
        test <@ attrInt root "lines-covered" = 2 @>
        test <@ attrInt root "branches-valid" = 4 @>
        test <@ attrInt root "branches-covered" = 2 @>
        // branch-rate = 0.5
        test <@ abs (attrFloat root "branch-rate" - 0.5) < 0.001 @>)

[<Fact>]
let ``recomputeRates with zero branches reports branch-rate of 1.0`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        let xml =
            buildCobertura
                [ { Package = "p"
                    Filename = "F.fs"
                    ClassName = "F"
                    Lines = [ line 1 1 ] } ]

        File.WriteAllText(baselinePath, xml)
        File.WriteAllText(partialPath, xml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath
        test <@ attrInt root "branches-valid" = 0 @>
        test <@ abs (attrFloat root "branch-rate" - 1.0) < 0.001 @>)

[<Fact>]
let ``mergeFiles preserves distinct method+signature variants (overloads)`` () =
    withTempDir (fun dir ->
        let baselinePath = Path.Combine(dir, "b.xml")
        let partialPath = Path.Combine(dir, "p.xml")
        let outPath = Path.Combine(dir, "o.xml")

        // Same method name "a" but different signatures — keyed separately.
        let baselineXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="(int)"><lines><line number="1" hits="1" branch="false" /></lines></method></methods></class></classes></package>"""

        let partialXml =
            buildWithPackages
                """<package name="p" line-rate="0" branch-rate="0"><classes><class name="C" filename="C.fs" line-rate="0" branch-rate="0"><methods><method name="a" signature="(string)"><lines><line number="5" hits="2" branch="false" /></lines></method></methods></class></classes></package>"""

        File.WriteAllText(baselinePath, baselineXml)
        File.WriteAllText(partialPath, partialXml)
        mergeFiles baselinePath partialPath outPath

        let root = loadRoot outPath

        let sigs =
            root.Descendants(xn "method")
            |> Seq.map (fun m -> m.Attribute(xn "signature").Value)
            |> Seq.toList
            |> List.sort

        test <@ sigs = [ "(int)"; "(string)" ] @>)

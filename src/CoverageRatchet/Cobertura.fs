module CoverageRatchet.Cobertura

open System.IO
open System.Xml.Linq
open System.Text.RegularExpressions

/// Per-file coverage data parsed from a Cobertura XML report.
type FileCoverage =
    { FileName: string
      LinePct: float
      BranchPct: float
      BranchesCovered: int
      BranchesTotal: int }

let private includedExtensions = [| ".fs" |]

let private excludedFileNamePatterns =
    [| "Test"; "AssemblyInfo"; "AssemblyAttributes" |]

let private excludedPathPatterns =
    [| "paket-files"; "vendor"; "node_modules"; ".fable" |]

let private branchRegex = Regex(@"\((\d+)/(\d+)\)", RegexOptions.Compiled)

let private isIncluded (fileName: string) =
    let hasValidExt = includedExtensions |> Array.exists fileName.EndsWith
    let baseName = Path.GetFileName(fileName)

    let isFileExcluded = excludedFileNamePatterns |> Array.exists baseName.Contains

    let segments =
        fileName.Split([| '/'; '\\' |], System.StringSplitOptions.RemoveEmptyEntries)

    let isPathExcluded =
        segments
        |> Array.exists (fun seg ->
            excludedPathPatterns
            |> Array.exists (fun p -> seg.Equals(p, System.StringComparison.OrdinalIgnoreCase)))

    hasValidExt && not isFileExcluded && not isPathExcluded

/// Raw line data extracted from a Cobertura XML class element.
type RawLine =
    { FileName: string
      LineNum: int
      WasHit: bool
      BrCovered: int
      BrTotal: int }

/// Extract raw per-class line data from XML content.
let extractRawLines (xmlContent: string) =
    let doc = XDocument.Parse(xmlContent)
    let ns = doc.Root.Name.Namespace

    doc.Root.Descendants(ns + "class")
    |> Seq.choose (fun classEl ->
        let fn = classEl.Attribute(XName.Get("filename"))

        if isNull fn || not (isIncluded fn.Value) then
            None
        else
            Some(fn.Value, classEl))
    |> Seq.collect (fun (fileName, classEl) ->
        let lines =
            classEl.Descendants(ns + "line")
            |> Seq.choose (fun line ->
                let numAttr = line.Attribute(XName.Get("number"))
                let hitsAttr = line.Attribute(XName.Get("hits"))

                if isNull numAttr || isNull hitsAttr then
                    None
                else
                    let cc = line.Attribute(XName.Get("condition-coverage"))

                    let brCovered, brTotal =
                        if isNull cc then
                            0, 0
                        else
                            let m = branchRegex.Match(cc.Value)

                            if m.Success then
                                int m.Groups.[1].Value, int m.Groups.[2].Value
                            else
                                0, 0

                    Some
                        { FileName = Path.GetFileName(fileName)
                          LineNum = int numAttr.Value
                          WasHit = int hitsAttr.Value > 0
                          BrCovered = brCovered
                          BrTotal = brTotal })
            |> Seq.toList

        if List.isEmpty lines then
            // Emit a placeholder so buildCoverage knows this file exists (zero-line class).
            // LineNum = -1 is filtered out by buildCoverage, resulting in 0 totalLines → 100%.
            Seq.singleton
                { FileName = Path.GetFileName(fileName)
                  LineNum = -1
                  WasHit = false
                  BrCovered = 0
                  BrTotal = 0 }
        else
            lines :> seq<_>)
    |> Seq.toList

/// Build FileCoverage list from raw line data.
let buildCoverage (rawLines: RawLine list) : FileCoverage list =
    rawLines
    |> List.groupBy (fun r -> r.FileName)
    |> List.map (fun (fileName, entries) ->
        let lineMap = System.Collections.Generic.Dictionary<int, bool>()
        let branchMap = System.Collections.Generic.Dictionary<int, int * int>()

        for r in entries |> List.filter (fun r -> r.LineNum >= 0) do
            match lineMap.TryGetValue(r.LineNum) with
            | true, existing -> lineMap.[r.LineNum] <- existing || r.WasHit
            | false, _ -> lineMap.[r.LineNum] <- r.WasHit

            if r.BrTotal > 0 then
                match branchMap.TryGetValue(r.LineNum) with
                | true, (existingC, existingT) ->
                    if r.BrCovered * existingT > existingC * r.BrTotal then
                        branchMap.[r.LineNum] <- (r.BrCovered, r.BrTotal)
                | false, _ -> branchMap.[r.LineNum] <- (r.BrCovered, r.BrTotal)

        let totalLines = lineMap.Count
        let coveredLines = lineMap.Values |> Seq.filter id |> Seq.length

        let linePct =
            if totalLines > 0 then
                float coveredLines / float totalLines * 100.0
            else
                100.0

        let coveredBranches = branchMap.Values |> Seq.sumBy fst
        let totalBranches = branchMap.Values |> Seq.sumBy snd

        let branchPct =
            if totalBranches > 0 then
                float coveredBranches / float totalBranches * 100.0
            else
                100.0

        { FileName = fileName
          LinePct = linePct
          BranchPct = branchPct
          BranchesCovered = coveredBranches
          BranchesTotal = totalBranches })

/// A single uncovered branch point on a specific line.
type BranchGap =
    { Line: int
      Covered: int
      Total: int }

/// Branch coverage gaps for a file.
type FileBranchGaps =
    { FileName: string
      BranchPct: float
      TotalBranches: int
      Gaps: BranchGap list }

/// Build per-file branch gap data from raw line data.
/// Returns only files that have at least one uncovered branch, sorted by gap count descending.
let buildBranchGaps (rawLines: RawLine list) : FileBranchGaps list =
    rawLines
    |> List.filter (fun r -> r.LineNum >= 0)
    |> List.groupBy (fun r -> r.FileName)
    |> List.choose (fun (fileName, entries) ->
        let branchMap = System.Collections.Generic.Dictionary<int, int * int>()

        for r in entries do
            if r.BrTotal > 0 then
                match branchMap.TryGetValue(r.LineNum) with
                | true, (existingC, existingT) ->
                    if r.BrCovered * existingT > existingC * r.BrTotal then
                        branchMap.[r.LineNum] <- (r.BrCovered, r.BrTotal)
                | false, _ -> branchMap.[r.LineNum] <- (r.BrCovered, r.BrTotal)

        let gaps =
            branchMap
            |> Seq.choose (fun kv ->
                let covered, total = kv.Value

                if covered < total then
                    Some { Line = kv.Key; Covered = covered; Total = total }
                else
                    None)
            |> Seq.sortBy (fun g -> g.Line)
            |> Seq.toList

        if List.isEmpty gaps then
            None
        else
            let coveredBranches = branchMap.Values |> Seq.sumBy fst
            let totalBranches = branchMap.Values |> Seq.sumBy snd

            let branchPct =
                if totalBranches > 0 then
                    float coveredBranches / float totalBranches * 100.0
                else
                    100.0

            Some
                { FileName = fileName
                  BranchPct = branchPct
                  TotalBranches = totalBranches
                  Gaps = gaps })
    |> List.sortByDescending (fun f -> f.Gaps.Length)

/// Parse Cobertura XML content string into FileCoverage list.
let parseXml (xmlContent: string) : FileCoverage list =
    extractRawLines xmlContent |> buildCoverage

/// Parse multiple Cobertura XML content strings and merge coverage across them.
/// Same files appearing in different XMLs have their line/branch data merged.
let parseXmls (xmlContents: string list) : FileCoverage list =
    xmlContents |> List.collect extractRawLines |> buildCoverage

/// Parse multiple Cobertura XML files from disk and merge coverage across them.
let parseFiles (xmlPaths: string list) : FileCoverage list =
    xmlPaths |> List.map File.ReadAllText |> parseXmls

/// Parse Cobertura XML from a file path.
let parseFile (xmlPath: string) : FileCoverage list =
    let content = File.ReadAllText(xmlPath)
    parseXml content

/// Find all coverage.cobertura.xml files in a directory (recursive).
let findCoverageFiles (searchDir: string) : string list =
    if Directory.Exists(searchDir) then
        Directory.GetFiles(searchDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
        |> Array.toList
    else
        []

/// Find most recent coverage.cobertura.xml in a directory (recursive).
let findCoverageFile (searchDir: string) : string option =
    findCoverageFiles searchDir
    |> List.sortByDescending File.GetLastWriteTime
    |> List.tryHead

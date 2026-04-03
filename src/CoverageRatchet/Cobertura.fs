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

let private excludedPatterns =
    [| "Test"; "AssemblyInfo"; "AssemblyAttributes" |]

let private branchRegex = Regex(@"\((\d+)/(\d+)\)", RegexOptions.Compiled)

let private isIncluded (fileName: string) =
    let hasValidExt =
        includedExtensions |> Array.exists fileName.EndsWith

    let isExcluded =
        excludedPatterns |> Array.exists fileName.Contains

    hasValidExt && not isExcluded

/// Parse Cobertura XML content string into FileCoverage list.
let parseXml (xmlContent: string) : FileCoverage list =
    let doc = XDocument.Parse(xmlContent)
    let ns = doc.Root.Name.Namespace

    doc.Root.Descendants(ns + "class")
    |> Seq.choose (fun classEl ->
        let fn = classEl.Attribute(XName.Get("filename"))

        if isNull fn || not (isIncluded fn.Value) then
            None
        else
            Some(fn.Value, classEl))
    |> Seq.toList
    |> List.groupBy fst
    |> List.map (fun (fileName, items) ->
        let classElements = items |> List.map snd

        let lineMap =
            System.Collections.Generic.Dictionary<int, bool>()

        let branchMap =
            System.Collections.Generic.Dictionary<int, int * int>()

        for classEl in classElements do
            for line in classEl.Descendants(ns + "line") do
                let numAttr = line.Attribute(XName.Get("number"))
                let hitsAttr = line.Attribute(XName.Get("hits"))

                if not (isNull numAttr) && not (isNull hitsAttr) then
                    let lineNum = int numAttr.Value
                    let hits = int hitsAttr.Value
                    let wasHit = hits > 0

                    match lineMap.TryGetValue(lineNum) with
                    | true, existing -> lineMap.[lineNum] <- existing || wasHit
                    | false, _ -> lineMap.[lineNum] <- wasHit

                    let cc =
                        line.Attribute(XName.Get("condition-coverage"))

                    if not (isNull cc) then
                        let m = branchRegex.Match(cc.Value)

                        if m.Success then
                            let covered = int m.Groups.[1].Value
                            let total = int m.Groups.[2].Value

                            match branchMap.TryGetValue(lineNum) with
                            | true, (existingC, existingT) ->
                                // Keep the entry with the better coverage ratio
                                if covered * existingT > existingC * total then
                                    branchMap.[lineNum] <- (covered, total)
                            | false, _ -> branchMap.[lineNum] <- (covered, total)

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

        { FileName = Path.GetFileName(fileName)
          LinePct = linePct
          BranchPct = branchPct
          BranchesCovered = coveredBranches
          BranchesTotal = totalBranches })

/// Parse Cobertura XML from a file path.
let parseFile (xmlPath: string) : FileCoverage list =
    let content = File.ReadAllText(xmlPath)
    parseXml content

/// Find most recent coverage.cobertura.xml in a directory (recursive).
let findCoverageFile (searchDir: string) : string option =
    if Directory.Exists(searchDir) then
        Directory.GetFiles(searchDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
        |> Array.sortByDescending File.GetLastWriteTime
        |> Array.tryHead
    else
        None

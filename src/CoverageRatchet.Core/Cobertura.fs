module CoverageRatchet.Cobertura

open System.IO
open System.Xml.Linq
open System.Text.RegularExpressions

// sync:file-coverage:start
/// Per-file coverage data parsed from a Cobertura XML report.
///
/// `*Total` counts drift between runs (a line is only emitted once its method is
/// JIT-compiled); `*Covered` counts are stable for unchanged code, which is why
/// count floors gate on them.
type FileCoverage =
    {
        FileName: string
        LinePct: float
        BranchPct: float
        LinesCovered: int
        LinesTotal: int
        BranchesCovered: int
        BranchesTotal: int
    }
// sync:file-coverage:end

// sync:reader-options:start
/// Which `<class>` elements of a Cobertura report the reader reads.
///
/// A file is read when its name ends with one of `IncludedExtensions`, its base name
/// contains none of `ExcludedFileNamePatterns` (case-sensitive substring), and no path
/// segment equals one of `ExcludedPathPatterns` (case-insensitive).
///
/// To read a C# report: `{ ReaderOptions.defaults with IncludedExtensions = [| ".cs" |] }`.
type ReaderOptions =
    {
        IncludedExtensions: string[]
        ExcludedFileNamePatterns: string[]
        ExcludedPathPatterns: string[]
    }
// sync:reader-options:end

module ReaderOptions =
    /// F# sources, minus test, generated and vendored files.
    let defaults =
        {
            IncludedExtensions = [| ".fs" |]
            ExcludedFileNamePatterns = [| "Test"; "AssemblyInfo"; "AssemblyAttributes" |]
            ExcludedPathPatterns = [| "paket-files"; "vendor"; "node_modules"; ".fable" |]
        }

let private branchRegex = Regex(@"\((\d+)/(\d+)\)", RegexOptions.Compiled)

// sync:exclusion-reason:start
/// Which `ReaderOptions` filter skipped a file, and the value that matched.
type ExclusionReason =
    | ExcludedByExtension of extension: string
    | ExcludedByFileName of pattern: string
    | ExcludedByPath of pattern: string

/// A file in the report that the reader skipped, keyed by base name like `FileCoverage`.
type ExcludedFile =
    {
        FileName: string
        Reason: ExclusionReason
    }
// sync:exclusion-reason:end

module ExclusionReason =
    /// e.g. `name contains "Test"`.
    let describe =
        function
        | ExcludedByExtension "" -> "has no extension"
        | ExcludedByExtension extension -> sprintf "extension \"%s\" is not read" extension
        | ExcludedByFileName pattern -> sprintf "name contains \"%s\"" pattern
        | ExcludedByPath pattern -> sprintf "under a \"%s\" path segment" pattern

/// `None` when the file is read. Checked in order extension, name, path, so the first
/// filter that matches is the reason reported.
let private classify (options: ReaderOptions) (fileName: string) : ExclusionReason option =
    if not (options.IncludedExtensions |> Array.exists fileName.EndsWith) then
        Some(ExcludedByExtension(Path.GetExtension(fileName)))
    else
        let baseName = Path.GetFileName(fileName)

        match options.ExcludedFileNamePatterns |> Array.tryFind baseName.Contains with
        | Some pattern -> Some(ExcludedByFileName pattern)
        | None ->
            let segments =
                fileName.Split([| '/'; '\\' |], System.StringSplitOptions.RemoveEmptyEntries)

            segments
            |> Array.tryPick (fun seg ->
                options.ExcludedPathPatterns
                |> Array.tryFind (fun p -> seg.Equals(p, System.StringComparison.OrdinalIgnoreCase)))
            |> Option.map ExcludedByPath

/// Raw line data extracted from a Cobertura XML class element.
type RawLine =
    {
        FileName: string
        LineNum: int
        WasHit: bool
        BrCovered: int
        BrTotal: int
    }

/// What the reader made of one or more Cobertura reports: every `<class>` with a
/// `filename` lands in exactly one of the two lists.
type Report =
    {
        Lines: RawLine list
        Excluded: ExcludedFile list
    }

let private readClassLines (fileName: string) (classEl: XElement) : RawLine list =
    let ns = classEl.Name.Namespace

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
                    {
                        FileName = Path.GetFileName(fileName)
                        LineNum = int numAttr.Value
                        WasHit = int hitsAttr.Value > 0
                        BrCovered = brCovered
                        BrTotal = brTotal
                    })
        |> Seq.toList

    if List.isEmpty lines then
        // Placeholder so a zero-line class still appears (as 100%); buildCoverage drops LineNum -1.
        [
            {
                FileName = Path.GetFileName(fileName)
                LineNum = -1
                WasHit = false
                BrCovered = 0
                BrTotal = 0
            }
        ]
    else
        lines

/// Read Cobertura XML reports in one pass, classifying each `<class>` once.
/// Exclusions are deduplicated by base name and sorted.
let readReports (options: ReaderOptions) (xmlContents: string list) : Report =
    let lines, excluded =
        xmlContents
        |> List.collect (fun xml ->
            let doc = XDocument.Parse(xml)

            doc.Root.Descendants(doc.Root.Name.Namespace + "class")
            |> Seq.choose (fun classEl ->
                let fn = classEl.Attribute(XName.Get("filename"))
                if isNull fn then None else Some(fn.Value, classEl))
            |> Seq.toList)
        |> List.partitionWith (fun (fileName, classEl) ->
            match classify options fileName with
            | None -> Choice1Of2(readClassLines fileName classEl)
            | Some reason ->
                Choice2Of2
                    {
                        FileName = Path.GetFileName(fileName)
                        Reason = reason
                    })

    {
        Lines = List.concat lines
        Excluded =
            excluded
            |> List.distinctBy (fun e -> e.FileName)
            |> List.sortBy (fun e -> e.FileName)
    }

/// Extract raw per-class line data from XML content, with `ReaderOptions.defaults`.
let extractRawLines (xmlContent: string) : RawLine list =
    (readReports ReaderOptions.defaults [ xmlContent ]).Lines

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

        {
            FileName = fileName
            LinePct = linePct
            BranchPct = branchPct
            LinesCovered = coveredLines
            LinesTotal = totalLines
            BranchesCovered = coveredBranches
            BranchesTotal = totalBranches
        })

/// A single uncovered branch point on a specific line.
type BranchGap = { Line: int; Covered: int; Total: int }

/// Branch coverage gaps for a file.
type FileBranchGaps =
    {
        FileName: string
        BranchPct: float
        TotalBranches: int
        Gaps: BranchGap list
    }

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
                    Some
                        {
                            Line = kv.Key
                            Covered = covered
                            Total = total
                        }
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
                {
                    FileName = fileName
                    BranchPct = branchPct
                    TotalBranches = totalBranches
                    Gaps = gaps
                })
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

let private excludedSearchDirs = Set.singleton ".devenv"

let private enumerateOptions =
    EnumerationOptions(IgnoreInaccessible = true, RecurseSubdirectories = false)

/// Find all coverage.cobertura.xml files in a directory (recursive).
/// Skips .devenv to avoid traversing Nix store symlinks.
let findCoverageFiles (searchDir: string) : string list =
    if Directory.Exists(searchDir) then
        let results = ResizeArray<string>()
        let queue = System.Collections.Generic.Queue<string>()
        queue.Enqueue(searchDir)

        while queue.Count > 0 do
            let dir = queue.Dequeue()
            let target = Path.Combine(dir, "coverage.cobertura.xml")

            if File.Exists(target) then
                results.Add(target)

            for sub in Directory.GetDirectories(dir, "*", enumerateOptions) do
                let name = Path.GetFileName(sub)

                if not (excludedSearchDirs.Contains(name)) then
                    queue.Enqueue(sub)

        results |> Seq.toList
    else
        []

/// Find most recent coverage.cobertura.xml in a directory (recursive).
let findCoverageFile (searchDir: string) : string option =
    findCoverageFiles searchDir
    |> List.sortByDescending File.GetLastWriteTime
    |> List.tryHead

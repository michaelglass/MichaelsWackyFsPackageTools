module CoverageRatchet.Merge

open System
open System.IO
open System.Xml
open System.Xml.Linq
open System.Globalization
open System.Collections.Generic

/// Merge two Cobertura XML reports, preferring max hit counts per line.
/// Designed to layer an impact-filtered partial test run onto a persisted
/// full-run baseline so the ratchet survives partial runs without needing
/// per-test attribution.
///
/// Merge rules:
/// - Union packages (by @name), classes (by @filename+@name), methods
///   (by @name+@signature), lines (by @number).
/// - line.hits = max(a.hits, b.hits); branch propagates if either is branch.
/// - condition union by @number; coverage picks max.
/// - condition-coverage picks whichever side has more covered (ties: more total).
/// - Summary rates (line-rate / branch-rate / *-covered / *-valid) recomputed
///   bottom-up from the merged lines.

let private xn (s: string) = XName.Get s

let private attr (name: string) (e: XElement) =
    match e.Attribute(xn name) with
    | null -> None
    | a -> Some a.Value

let private attrOr d n e = defaultArg (attr n e) d

let private setAttr (name: string) (value: string) (e: XElement) = e.SetAttributeValue(xn name, value)

let private parseInt (s: string) =
    match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> 0

let private parseFloat (s: string) =
    match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> 0.0

let private parseCondCoverage (s: string) : (int * int) option =
    let s = s.Trim()
    let paren = s.IndexOf '('

    if paren >= 0 then
        let inner = s.Substring(paren + 1).TrimEnd(')').Trim()
        let parts = inner.Split('/')

        if parts.Length = 2 then
            match Int32.TryParse(parts.[0]), Int32.TryParse(parts.[1]) with
            | (true, a), (true, b) when b > 0 -> Some(a, b)
            | _ -> None
        else
            None
    else
        None

let private formatCondCoverage (covered: int) (total: int) =
    let pct =
        if total = 0 then
            100.0
        else
            100.0 * float covered / float total

    sprintf "%s%% (%d/%d)" (pct.ToString("0.##", CultureInfo.InvariantCulture)) covered total

let private mergeLine (a: XElement) (b: XElement) =
    let aHits = attrOr "0" "hits" a |> parseInt
    let bHits = attrOr "0" "hits" b |> parseInt
    setAttr "hits" (string (max aHits bHits)) a

    let aBranch = (attrOr "false" "branch" a).ToLowerInvariant() = "true"
    let bBranch = (attrOr "false" "branch" b).ToLowerInvariant() = "true"

    if aBranch || bBranch then
        setAttr "branch" "true" a
        let aConds = a.Element(xn "conditions")
        let bConds = b.Element(xn "conditions")
        let cache = Dictionary<string, XElement>()

        if aConds <> null then
            for c in aConds.Elements(xn "condition") do
                cache.[attrOr "" "number" c] <- c

        if bConds <> null then
            for c in bConds.Elements(xn "condition") do
                let n = attrOr "" "number" c

                match cache.TryGetValue n with
                | true, existing ->
                    let eCov = attrOr "0%" "coverage" existing
                    let nCov = attrOr "0%" "coverage" c
                    let parse (s: string) = s.TrimEnd('%') |> parseFloat

                    if parse nCov > parse eCov then
                        setAttr "coverage" nCov existing
                | _ -> cache.[n] <- XElement(c)

        if cache.Count > 0 then
            let condsEl = XElement(xn "conditions")

            for kv in cache |> Seq.sortBy (fun kv -> parseInt kv.Key) do
                condsEl.Add kv.Value

            if aConds <> null then
                aConds.Remove()

            a.Add condsEl

        let aCov = attrOr "" "condition-coverage" a |> parseCondCoverage
        let bCov = attrOr "" "condition-coverage" b |> parseCondCoverage

        let best =
            match aCov, bCov with
            | Some(ac, at), Some(bc, bt) ->
                if ac > bc then Some(ac, at)
                elif bc > ac then Some(bc, bt)
                else Some(max ac bc, max at bt)
            | Some v, None
            | None, Some v -> Some v
            | None, None -> None

        match best with
        | Some(c, t) -> setAttr "condition-coverage" (formatCondCoverage c t) a
        | None -> ()

    a

let private mergeLinesInto (target: XElement) (source: XElement) =
    let lines = Dictionary<int, XElement>()

    for l in target.Elements(xn "line") do
        lines.[attrOr "0" "number" l |> parseInt] <- l

    for l in source.Elements(xn "line") do
        let n = attrOr "0" "number" l |> parseInt

        match lines.TryGetValue n with
        | true, existing -> mergeLine existing l |> ignore
        | _ ->
            let clone = XElement(l)
            lines.[n] <- clone
            target.Add clone

let private mergeMethods (aMethods: XElement) (bMethods: XElement) =
    if bMethods <> null then
        let key (m: XElement) =
            (attrOr "" "name" m) + "|" + (attrOr "" "signature" m)

        let cache = Dictionary<string, XElement>()

        for m in aMethods.Elements(xn "method") do
            cache.[key m] <- m

        for m in bMethods.Elements(xn "method") do
            let k = key m

            match cache.TryGetValue k with
            | true, existing ->
                let aLines = existing.Element(xn "lines")
                let bLines = m.Element(xn "lines")

                if bLines <> null then
                    let al =
                        if aLines = null then
                            let el = XElement(xn "lines")
                            existing.Add el
                            el
                        else
                            aLines

                    mergeLinesInto al bLines
            | _ -> aMethods.Add(XElement(m))

let private mergeClass (a: XElement) (b: XElement) =
    let am = a.Element(xn "methods")
    let bm = b.Element(xn "methods")

    if bm <> null then
        let am =
            if am = null then
                let e = XElement(xn "methods")
                a.Add e
                e
            else
                am

        mergeMethods am bm

    let al = a.Element(xn "lines")
    let bl = b.Element(xn "lines")

    if bl <> null then
        let al =
            if al = null then
                let e = XElement(xn "lines")
                a.Add e
                e
            else
                al

        mergeLinesInto al bl

let private mergeClasses (aClasses: XElement) (bClasses: XElement) =
    if bClasses <> null then
        let key (c: XElement) =
            attrOr "" "filename" c + "|" + attrOr "" "name" c

        let cache = Dictionary<string, XElement>()

        for c in aClasses.Elements(xn "class") do
            cache.[key c] <- c

        for c in bClasses.Elements(xn "class") do
            let k = key c

            match cache.TryGetValue k with
            | true, existing -> mergeClass existing c
            | _ -> aClasses.Add(XElement(c))

let private mergePackages (aPkgs: XElement) (bPkgs: XElement) =
    if bPkgs <> null then
        let cache = Dictionary<string, XElement>()

        for p in aPkgs.Elements(xn "package") do
            cache.[attrOr "" "name" p] <- p

        for p in bPkgs.Elements(xn "package") do
            let n = attrOr "" "name" p

            match cache.TryGetValue n with
            | true, existing ->
                let ac = existing.Element(xn "classes")
                let bc = p.Element(xn "classes")

                if bc <> null then
                    let ac =
                        if ac = null then
                            let e = XElement(xn "classes")
                            existing.Add e
                            e
                        else
                            ac

                    mergeClasses ac bc
            | _ -> aPkgs.Add(XElement(p))

let private classLines (cls: XElement) =
    seq {
        let m = cls.Element(xn "methods")

        if m <> null then
            for meth in m.Elements(xn "method") do
                let ls = meth.Element(xn "lines")

                if ls <> null then
                    yield! ls.Elements(xn "line")

        let ls = cls.Element(xn "lines")

        if ls <> null then
            yield! ls.Elements(xn "line")
    }

let private recomputeRates (root: XElement) =
    let mutable rootLinesCov, rootLinesTot = 0, 0
    let mutable rootBranchesCov, rootBranchesTot = 0, 0
    let pkgs = root.Element(xn "packages")

    if pkgs <> null then
        for pkg in pkgs.Elements(xn "package") do
            let mutable pLC, pLT, pBC, pBT = 0, 0, 0, 0
            let classes = pkg.Element(xn "classes")

            if classes <> null then
                for cls in classes.Elements(xn "class") do
                    let lines =
                        classLines cls
                        |> Seq.distinctBy (fun l -> attrOr "0" "number" l |> parseInt)
                        |> Seq.toList

                    let linesTot = lines.Length

                    let linesCov =
                        lines
                        |> List.filter (fun l -> (attrOr "0" "hits" l |> parseInt) > 0)
                        |> List.length

                    let branches =
                        lines
                        |> List.filter (fun l -> (attrOr "false" "branch" l).ToLowerInvariant() = "true")

                    let mutable bC, bT = 0, 0

                    for l in branches do
                        match attrOr "" "condition-coverage" l |> parseCondCoverage with
                        | Some(c, t) ->
                            bC <- bC + c
                            bT <- bT + t
                        | None -> ()

                    let lr =
                        if linesTot = 0 then
                            1.0
                        else
                            float linesCov / float linesTot

                    let br = if bT = 0 then 1.0 else float bC / float bT

                    setAttr "line-rate" (lr.ToString("0.####", CultureInfo.InvariantCulture)) cls
                    setAttr "branch-rate" (br.ToString("0.####", CultureInfo.InvariantCulture)) cls
                    pLC <- pLC + linesCov
                    pLT <- pLT + linesTot
                    pBC <- pBC + bC
                    pBT <- pBT + bT

            let plr = if pLT = 0 then 1.0 else float pLC / float pLT

            let pbr = if pBT = 0 then 1.0 else float pBC / float pBT

            setAttr "line-rate" (plr.ToString("0.####", CultureInfo.InvariantCulture)) pkg
            setAttr "branch-rate" (pbr.ToString("0.####", CultureInfo.InvariantCulture)) pkg
            rootLinesCov <- rootLinesCov + pLC
            rootLinesTot <- rootLinesTot + pLT
            rootBranchesCov <- rootBranchesCov + pBC
            rootBranchesTot <- rootBranchesTot + pBT

    let rlr =
        if rootLinesTot = 0 then
            1.0
        else
            float rootLinesCov / float rootLinesTot

    let rbr =
        if rootBranchesTot = 0 then
            1.0
        else
            float rootBranchesCov / float rootBranchesTot

    setAttr "line-rate" (rlr.ToString("0.####", CultureInfo.InvariantCulture)) root
    setAttr "branch-rate" (rbr.ToString("0.####", CultureInfo.InvariantCulture)) root
    setAttr "lines-covered" (string rootLinesCov) root
    setAttr "lines-valid" (string rootLinesTot) root
    setAttr "branches-covered" (string rootBranchesCov) root
    setAttr "branches-valid" (string rootBranchesTot) root

/// Merge `partialPath` into `baselinePath` and write the result to `outputPath`.
/// If baseline doesn't exist, the partial is used as the initial baseline.
/// Returns unit; throws on IO errors.
let mergeFiles (baselinePath: string) (partialPath: string) (outputPath: string) =
    let existsB = File.Exists baselinePath

    let a =
        if existsB then
            XDocument.Load(baselinePath).Root
        else
            XDocument.Load(partialPath).Root

    if existsB then
        let b = XDocument.Load(partialPath).Root
        let aPkgs = a.Element(xn "packages")
        let bPkgs = b.Element(xn "packages")

        if aPkgs = null then
            let e = XElement(xn "packages")
            a.Add e
            mergePackages e bPkgs
        else
            mergePackages aPkgs bPkgs

    recomputeRates a

    let settings =
        XmlWriterSettings(Indent = true, IndentChars = "  ", OmitXmlDeclaration = false)

    use w = XmlWriter.Create(outputPath, settings)
    a.Document.Save(w)

/// For each coverage.cobertura.xml in the search dir, if a sibling
/// coverage.baseline.xml exists, merge into it (writing back to
/// coverage.cobertura.xml). If no baseline exists, copy the current run as
/// the initial baseline. Used by `check --merge-baselines`.
let mergeIntoBaselines (searchDir: string) : unit =
    for coverageFile in Cobertura.findCoverageFiles searchDir do
        let dir = Path.GetDirectoryName(coverageFile)
        let baseline = Path.Combine(dir, "coverage.baseline.xml")

        if File.Exists baseline then
            let tmp = Path.Combine(dir, "coverage.merged.xml")

            try
                mergeFiles baseline coverageFile tmp
                File.Move(tmp, coverageFile, overwrite = true)
            with ex ->
                raise (exn ($"Failed to merge baseline for project {dir}: {ex.Message}", ex))
        else
            File.Copy(coverageFile, baseline, overwrite = false)

/// For each coverage.cobertura.xml in the search dir, copy it to
/// coverage.baseline.xml. Used after a known-full test run to advance
/// the baseline so subsequent partial runs merge against up-to-date data.
let refreshBaselines (searchDir: string) : unit =
    for coverageFile in Cobertura.findCoverageFiles searchDir do
        let dir = Path.GetDirectoryName(coverageFile)
        let baseline = Path.Combine(dir, "coverage.baseline.xml")
        File.Copy(coverageFile, baseline, overwrite = true)

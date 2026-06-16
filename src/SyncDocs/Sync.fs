module SyncDocs.Sync

open System.IO
open System.Text.RegularExpressions

// sync:outcome-types:start
type SyncMode =
    | Check
    | Apply

type SyncOutcome =
    | InSync
    | OutOfSync
    | Updated
// sync:outcome-types:end

type SyncError =
    | SourceMissing of string
    | TargetMissing of string

/// Why a code region could not be extracted from a referenced .fs/.fsx file.
type RegionError =
    | RegionMissing of region: string
    | RegionDuplicated of region: string
    | RegionUnterminated of region: string

/// Why a code-sourced README block could not be resolved.
type CodeSyncError =
    | CodeFileMissing of path: string
    | CodeRegionError of path: string * error: RegionError

type SyncPair = { Source: string; Target: string }

type DiscoveryWarning =
    | MissingTarget of name: string * suggestedPath: string
    | MissingSource of name: string * suggestedPath: string

type DiscoveryResult =
    { Pairs: SyncPair list
      Warnings: DiscoveryWarning list }

/// Extract tagged sections from source (README) content.
/// Source uses: <!-- sync:name:start -->...<!-- sync:name:end -->
/// A start marker may also carry a `src=...` code-region attribute, which is
/// ignored here: a code-sourced block is still an ordinary section whose body
/// (the rendered fenced snippet) propagates to docs like any other.
let extractSections (content: string) : Map<string, string> =
    let pattern =
        @"<!-- sync:([\w][\w-]*):start(?: src=\S+?)? -->([\s\S]*?)<!-- sync:\1:end -->"

    Regex.Matches(content, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Map.ofSeq

/// Extract a code region delimited by comment markers from a .fs/.fsx file.
/// The file uses: // sync:region:start ... // sync:region:end
/// Returns the lines BETWEEN the markers (markers excluded), with the common
/// leading indentation stripped. Blank lines are preserved verbatim and do not
/// participate in the dedent calculation.
let extractRegion (content: string) (region: string) : Result<string list, RegionError> =
    let lines =
        content.Replace("\r\n", "\n").Split('\n') |> Array.toList |> List.indexed

    let indicesOf (suffix: string) =
        let marker = sprintf "// sync:%s:%s" region suffix

        lines
        |> List.choose (fun (i, line) -> if line.Trim() = marker then Some i else None)

    match indicesOf "start", indicesOf "end" with
    | [], _ -> Error(RegionMissing region)
    | (_ :: _ :: _), _
    | _, (_ :: _ :: _) -> Error(RegionDuplicated region)
    | [ _ ], [] -> Error(RegionUnterminated region)
    | [ startIdx ], [ endIdx ] when endIdx < startIdx -> Error(RegionUnterminated region)
    | [ startIdx ], [ endIdx ] ->
        let body =
            lines
            |> List.choose (fun (i, line) -> if i > startIdx && i < endIdx then Some line else None)

        // Common leading whitespace across non-blank lines.
        let leadingWhitespace (line: string) =
            line
            |> Seq.takeWhile (fun c -> c = ' ' || c = '\t')
            |> Seq.toArray
            |> System.String

        let commonIndent =
            body
            |> List.filter (fun l -> l.Trim() <> "")
            |> List.map leadingWhitespace
            |> function
                | [] -> ""
                | first :: rest ->
                    let common (a: string) (b: string) =
                        let n = min a.Length b.Length

                        seq { 0 .. n - 1 }
                        |> Seq.takeWhile (fun i -> a.[i] = b.[i])
                        |> Seq.length
                        |> fun len -> a.Substring(0, len)

                    List.fold common first rest

        body
        |> List.map (fun line ->
            if line.StartsWith commonIndent then
                line.Substring(commonIndent.Length)
            else
                line)
        |> Ok

/// Replace tagged sections in target (docs) content with new content from source.
/// Target uses: <!-- sync:name -->...<!-- sync:name:end --> (no :start suffix).
let replaceSections (content: string) (sections: Map<string, string>) : string =
    sections
    |> Map.fold
        (fun (acc: string) name newContent ->
            let pattern =
                sprintf @"(<!-- sync:%s -->)[ \t]*\n[\s\S]*?(<!-- sync:%s:end -->)" name name

            let escaped = newContent.Replace("$", "$$")
            Regex.Replace(acc, pattern, sprintf "$1%s$2" escaped))
        content

/// Render extracted code-region lines as a fenced F# code block, with the
/// surrounding blank lines that the sync markers expect around a body.
let renderCodeBlock (lines: string list) : string =
    sprintf "\n```fsharp\n%s\n```\n" (String.concat "\n" lines)

/// A README block that sources its body from a region of a real .fs/.fsx file.
type private CodeBlock =
    { Name: string
      RelativePath: string
      Region: string }

/// Parse `src=path` / `src=path#region` attributes from README start markers.
/// Region defaults to the block name when no `#region` override is given.
let private extractCodeBlocks (readme: string) : CodeBlock list =
    let pattern = @"<!-- sync:([\w][\w-]*):start src=(\S+?) -->"

    Regex.Matches(readme, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m ->
        let name = m.Groups.[1].Value
        let src = m.Groups.[2].Value

        let path, region =
            match src.IndexOf '#' with
            | -1 -> src, name
            | hashIdx -> src.Substring(0, hashIdx), src.Substring(hashIdx + 1)

        { Name = name
          RelativePath = path
          Region = region })
    |> Seq.toList

/// Replace the body of a single code-sourced block, preserving its start marker
/// (with its `src=` attribute) and end marker. $ is escaped for the regex
/// replacement, mirroring replaceSections.
let private replaceCodeBlock (readme: string) (name: string) (newBody: string) : string =
    let pattern =
        sprintf @"(<!-- sync:%s:start src=\S+? -->)[\s\S]*?(<!-- sync:%s:end -->)" name name

    let escaped = newBody.Replace("$", "$$")
    Regex.Replace(readme, pattern, sprintf "$1%s$2" escaped)

/// The current body of a code-sourced block (between its start/end markers).
let private currentCodeBody (readme: string) (name: string) : string option =
    let pattern =
        sprintf @"<!-- sync:%s:start src=\S+? -->([\s\S]*?)<!-- sync:%s:end -->" name name

    let m = Regex.Match(readme, pattern)
    if m.Success then Some m.Groups.[1].Value else None

/// Refresh every code-sourced block in a README from its referenced file region.
/// Runs ON the README itself, BEFORE the README->docs propagation, so docs pick
/// up the refreshed snippet. Fails loudly on a missing file or region error.
let syncCodeRegions (mode: SyncMode) (rootDir: string) (readmePath: string) : Result<SyncOutcome, CodeSyncError> =
    let readme = File.ReadAllText readmePath
    let blocks = extractCodeBlocks readme

    let resolveBody (block: CodeBlock) : Result<string, CodeSyncError> =
        let fullPath = Path.Combine(rootDir, block.RelativePath)

        if not (File.Exists fullPath) then
            Error(CodeFileMissing block.RelativePath)
        else
            match extractRegion (File.ReadAllText fullPath) block.Region with
            | Error regionErr -> Error(CodeRegionError(block.RelativePath, regionErr))
            | Ok lines -> Ok(renderCodeBlock lines)

    let folder (state: Result<string * bool, CodeSyncError>) (block: CodeBlock) =
        state
        |> Result.bind (fun (content, anyChange) ->
            resolveBody block
            |> Result.map (fun freshBody ->
                let current = currentCodeBody content block.Name |> Option.defaultValue ""

                if current = freshBody then
                    content, anyChange
                else
                    replaceCodeBlock content block.Name freshBody, true))

    match List.fold folder (Ok(readme, false)) blocks with
    | Error err -> Error err
    | Ok(_, false) -> Ok InSync
    | Ok(_, true) when mode = Check -> Ok OutOfSync
    | Ok(updated, true) ->
        File.WriteAllText(readmePath, updated)
        Ok Updated

/// Sync a single source->target pair.
let syncPair (mode: SyncMode) (sourcePath: string) (targetPath: string) : Result<SyncOutcome, SyncError> =
    if not (File.Exists sourcePath) then
        Error(SourceMissing sourcePath)
    elif not (File.Exists targetPath) then
        Error(TargetMissing targetPath)
    else
        let sourceContent = File.ReadAllText sourcePath
        let sections = extractSections sourceContent

        if sections.IsEmpty then
            // Full-file sync
            let targetContent = File.ReadAllText targetPath

            if sourceContent = targetContent then
                Ok InSync
            elif mode = Check then
                Ok OutOfSync
            else
                File.WriteAllText(targetPath, sourceContent)
                Ok Updated
        else
            let targetContent = File.ReadAllText targetPath
            let replaced = replaceSections targetContent sections

            if replaced = targetContent then
                Ok InSync
            elif mode = Check then
                Ok OutOfSync
            else
                File.WriteAllText(targetPath, replaced)
                Ok Updated

/// Enumerate all conventional candidate pairs.
let private candidatePairs (rootDir: string) : (string * SyncPair) list =
    let root =
        [ "your project",
          { Source = Path.Combine(rootDir, "README.md")
            Target = Path.Combine(rootDir, "docs", "index.md") } ]

    let srcDir = Path.Combine(rootDir, "src")

    let srcPairs =
        if Directory.Exists srcDir then
            Directory.GetDirectories(srcDir)
            |> Array.toList
            |> List.map (fun dir ->
                let dirName = Path.GetFileName dir

                dirName,
                { Source = Path.Combine(dir, "README.md")
                  Target = Path.Combine(rootDir, "docs", dirName, "index.md") })
        else
            []

    root @ srcPairs

/// Discover sync pairs and warnings in a single pass over candidates.
/// README.md -> docs/index.md, src/*/README.md -> docs/*/index.md
let discoverPairsAndWarnings (rootDir: string) : DiscoveryResult =
    candidatePairs rootDir
    |> List.fold
        (fun (pairs, warnings) (name, pair) ->
            let srcExists = File.Exists pair.Source
            let tgtExists = File.Exists pair.Target

            let pairs = if srcExists && tgtExists then pair :: pairs else pairs

            let warnings =
                if srcExists && not tgtExists then
                    MissingTarget(name, Path.GetRelativePath(rootDir, pair.Target)) :: warnings
                elif tgtExists && not srcExists then
                    MissingSource(name, Path.GetRelativePath(rootDir, pair.Source)) :: warnings
                else
                    warnings

            pairs, warnings)
        ([], [])
    |> fun (pairs, warnings) ->
        { Pairs = List.rev pairs
          Warnings = List.rev warnings }

let discoverPairs (rootDir: string) : SyncPair list =
    (discoverPairsAndWarnings rootDir).Pairs

let discoverWarnings (rootDir: string) : DiscoveryWarning list =
    (discoverPairsAndWarnings rootDir).Warnings

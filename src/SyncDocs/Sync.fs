module SyncDocs.Sync

open System.IO
open System.Text.RegularExpressions

type SyncMode =
    | Check
    | Apply

type SyncOutcome =
    | InSync
    | OutOfSync
    | Updated

type SyncError =
    | SourceMissing of string
    | TargetMissing of string

type SyncPair = { Source: string; Target: string }

type DiscoveryWarning =
    | MissingTarget of name: string * suggestedPath: string
    | MissingSource of name: string * suggestedPath: string

type DiscoveryResult =
    { Pairs: SyncPair list
      Warnings: DiscoveryWarning list }

/// Extract tagged sections from source (README) content.
/// Source uses: <!-- sync:name:start -->...<!-- sync:name:end -->
let extractSections (content: string) : Map<string, string> =
    let pattern = @"<!-- sync:([\w][\w-]*):start -->([\s\S]*?)<!-- sync:\1:end -->"

    Regex.Matches(content, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Map.ofSeq

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
    [ yield
          "your project",
          { Source = Path.Combine(rootDir, "README.md")
            Target = Path.Combine(rootDir, "docs", "index.md") }

      let srcDir = Path.Combine(rootDir, "src")

      if Directory.Exists srcDir then
          for dir in Directory.GetDirectories(srcDir) do
              let dirName = Path.GetFileName dir

              yield
                  dirName,
                  { Source = Path.Combine(dir, "README.md")
                    Target = Path.Combine(rootDir, "docs", dirName, "index.md") } ]

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

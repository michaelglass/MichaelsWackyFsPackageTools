module SyncDocs.Sync

open System.IO
open System.Text.RegularExpressions

type SyncResult =
    | InSync
    | OutOfSync
    | Updated
    | SourceMissing
    | TargetMissing

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
let syncPair (check: bool) (sourcePath: string) (targetPath: string) : SyncResult =
    if not (File.Exists sourcePath) then
        SourceMissing
    elif not (File.Exists targetPath) then
        TargetMissing
    else
        let sourceContent = File.ReadAllText sourcePath
        let sections = extractSections sourceContent

        if sections.IsEmpty then
            // Full-file sync
            let targetContent = File.ReadAllText targetPath

            if sourceContent = targetContent then
                InSync
            elif check then
                OutOfSync
            else
                File.WriteAllText(targetPath, sourceContent)
                Updated
        else
            let targetContent = File.ReadAllText targetPath
            let replaced = replaceSections targetContent sections

            if replaced = targetContent then
                InSync
            elif check then
                OutOfSync
            else
                File.WriteAllText(targetPath, replaced)
                Updated

/// Enumerate all conventional (name, source, target) candidates.
let private candidatePairs (rootDir: string) : (string * string * string) list =
    [ yield "your project", Path.Combine(rootDir, "README.md"), Path.Combine(rootDir, "docs", "index.md")

      let srcDir = Path.Combine(rootDir, "src")

      if Directory.Exists srcDir then
          for dir in Directory.GetDirectories(srcDir) do
              let dirName = Path.GetFileName dir

              yield dirName, Path.Combine(dir, "README.md"), Path.Combine(rootDir, "docs", dirName, "index.md") ]

/// Discover sync pairs and warnings in a single pass over candidates.
/// README.md -> docs/index.md, src/*/README.md -> docs/*/index.md
let discoverPairsAndWarnings (rootDir: string) : (string * string) list * string list =
    candidatePairs rootDir
    |> List.fold
        (fun (pairs, warnings) (name, src, tgt) ->
            let srcExists = File.Exists src
            let tgtExists = File.Exists tgt

            let pairs =
                if srcExists && tgtExists then
                    (src, tgt) :: pairs
                else
                    pairs

            let warnings =
                if srcExists && not tgtExists then
                    sprintf "To sync docs for %s, create %s" name (Path.GetRelativePath(rootDir, tgt))
                    :: warnings
                elif tgtExists && not srcExists then
                    sprintf "To sync docs for %s, create %s" name (Path.GetRelativePath(rootDir, src))
                    :: warnings
                else
                    warnings

            pairs, warnings)
        ([], [])
    |> fun (pairs, warnings) -> List.rev pairs, List.rev warnings

let discoverPairs (rootDir: string) : (string * string) list = discoverPairsAndWarnings rootDir |> fst

let discoverWarnings (rootDir: string) : string list = discoverPairsAndWarnings rootDir |> snd

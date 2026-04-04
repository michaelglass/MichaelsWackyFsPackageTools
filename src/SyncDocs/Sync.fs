module SyncDocs.Sync

open System.IO
open System.Text.RegularExpressions

type SyncResult =
    | InSync
    | OutOfSync
    | Updated
    | SourceMissing
    | TargetMissing

/// Extract tagged sections from source content.
/// Tags: <!-- sync:name:start -->...<!-- sync:name:end -->
let extractSections (content: string) : Map<string, string> =
    let pattern = @"<!-- sync:([\w][\w-]*):start -->([\s\S]*?)<!-- sync:\1:end -->"

    Regex.Matches(content, pattern)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
    |> Map.ofSeq

/// Replace tagged sections in target content with new content.
/// Tags: <!-- sync:name -->...<!-- sync:name:end -->
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

/// Discover sync pairs by convention.
/// README.md -> docs/index.md, src/*/README.md -> docs/*/index.md
let discoverPairs (rootDir: string) : (string * string) list =
    let pairs = ResizeArray<string * string>()

    // Root README.md -> docs/index.md
    let rootReadme = Path.Combine(rootDir, "README.md")
    let rootTarget = Path.Combine(rootDir, "docs", "index.md")

    if File.Exists rootReadme && File.Exists rootTarget then
        pairs.Add(rootReadme, rootTarget)

    // src/*/README.md -> docs/*/index.md
    let srcDir = Path.Combine(rootDir, "src")

    if Directory.Exists srcDir then
        for dir in Directory.GetDirectories(srcDir) do
            let readme = Path.Combine(dir, "README.md")
            let dirName = Path.GetFileName dir
            let target = Path.Combine(rootDir, "docs", dirName, "index.md")

            if File.Exists readme && File.Exists target then
                pairs.Add(readme, target)

    pairs |> Seq.toList

/// Discover incomplete sync pairs and return friendly warning messages.
let discoverWarnings (rootDir: string) : string list =
    let warnings = ResizeArray<string>()

    // Root level: README.md and docs/index.md
    let rootReadme = Path.Combine(rootDir, "README.md")
    let rootTarget = Path.Combine(rootDir, "docs", "index.md")

    if File.Exists rootReadme && not (File.Exists rootTarget) then
        warnings.Add("To sync docs for your project, create docs/index.md")
    elif File.Exists rootTarget && not (File.Exists rootReadme) then
        warnings.Add("To sync docs for your project, create README.md")

    // src/*/: For each subdirectory in src/
    let srcDir = Path.Combine(rootDir, "src")

    if Directory.Exists srcDir then
        for dir in Directory.GetDirectories(srcDir) do
            let dirName = Path.GetFileName dir
            let readme = Path.Combine(dir, "README.md")
            let target = Path.Combine(rootDir, "docs", dirName, "index.md")

            if File.Exists readme && not (File.Exists target) then
                warnings.Add(sprintf "To sync docs for %s, create docs/%s/index.md" dirName dirName)
            elif File.Exists target && not (File.Exists readme) then
                warnings.Add(sprintf "To sync docs for %s, create src/%s/README.md" dirName dirName)

    warnings |> Seq.toList

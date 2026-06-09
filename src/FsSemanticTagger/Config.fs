module FsSemanticTagger.Config

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq

type PackageConfig =
    { Name: string
      Fsproj: string
      DllPath: string
      TagPrefix: string
      FsProjsSharingSameTag: string list }

type ToolConfig =
    { Packages: PackageConfig list
      ReservedVersions: Set<string>
      PreBuildCmds: string list
      RootDir: string }

let private assemblyNameRegex =
    Regex(@"<AssemblyName>([^<]+)</AssemblyName>", RegexOptions.Compiled)

let private packageIdRegex =
    Regex(@"<PackageId>([^<]+)</PackageId>", RegexOptions.Compiled)

let private projectReferenceIncludeRegex =
    Regex(
        """<ProjectReference\b[^>]*?\bInclude\s*=\s*["']([^"']+)["']""",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase
    )

let private isPackableFalseRegex =
    Regex(@"<IsPackable>\s*false\s*</IsPackable>", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Derive the DLL output path from an fsproj path and its content.
let deriveDllPathFromContent (fsprojPath: string) (content: string) : string =
    let dir = Path.GetDirectoryName(fsprojPath)
    let assemblyNameMatch = assemblyNameRegex.Match(content)

    let name =
        if assemblyNameMatch.Success then
            assemblyNameMatch.Groups[1].Value
        else
            Path.GetFileNameWithoutExtension(fsprojPath)

    Path.Combine(dir, "bin", "Release", "net10.0", name + ".dll")

/// Derive the DLL path from an fsproj file path (reads the file).
let deriveDllPath (fsprojPath: string) : string =
    deriveDllPathFromContent fsprojPath (File.ReadAllText(fsprojPath))

/// Parse the `Include` values of every `<ProjectReference>` from fsproj XML
/// content. Pure (no I/O). Handles both self-closing
/// (`<ProjectReference Include="..." />`) and open/close
/// (`<ProjectReference Include="...">...</ProjectReference>`) forms,
/// single- or double-quoted attribute values, and arbitrary surrounding
/// whitespace. The raw Include string is returned verbatim (back- or
/// forward-slashes preserved); path normalisation is the caller's job.
let parseProjectReferenceIncludes (content: string) : string list =
    [ for m in projectReferenceIncludeRegex.Matches(content) -> m.Groups[1].Value ]

/// Normalise an arbitrary path to a repo-root-relative directory using forward
/// slashes and no trailing slash. `rootDir` and `absolutePath` are both
/// resolved to full paths first so the relative result is stable.
let private toRepoRelativeDir (rootDir: string) (absolutePath: string) : string =
    let full = Path.GetFullPath(absolutePath)
    let dir = Path.GetDirectoryName(full)

    Path.GetRelativePath(Path.GetFullPath(rootDir), dir).Replace('\\', '/').TrimEnd('/')

/// Resolve the transitive `<ProjectReference>` closure of `fsprojRelPath`
/// (relative to `rootDir`), returned as repo-root-relative directory paths
/// (forward slashes, no trailing slash).
///
/// Each Include is resolved relative to the *referencing* fsproj's directory,
/// then normalised against `rootDir`. Traversal is transitive (a referenced
/// project's own ProjectReferences are followed too), de-duplicated, and
/// cycle-safe (each fsproj is visited at most once). A referenced fsproj that
/// does not exist on disk is skipped silently rather than throwing. The
/// package's *own* directory is excluded — change-detection for the package
/// itself is handled separately by the caller. Results are returned in a
/// stable de-duplicated order (breadth-first discovery order: a project's
/// direct references before their transitive ones).
///
/// Note: this treats EVERY ProjectReference as change-relevant. That is exact
/// for a package that *bundles* its references (a `PackAsTool` ships their
/// DLLs, so a dependency change genuinely changes the published artifact). A
/// non-tool library that ProjectReferences a separately-published packable
/// dependency (consumed by its own NuGet PackageReference, not bundled) will
/// over-trigger a redundant — but harmless — rebundle; better that than miss a
/// real change.
let transitiveProjectRefDirs (rootDir: string) (fsprojRelPath: string) : string list =
    let rootFsprojFull = Path.GetFullPath(Path.Combine(rootDir, fsprojRelPath))
    let ownDir = toRepoRelativeDir rootDir rootFsprojFull

    let visited = System.Collections.Generic.HashSet<string>()
    let ordered = ResizeArray<string>()
    let seenDirs = System.Collections.Generic.HashSet<string>()

    // Breadth-first traversal: a project's *direct* references are recorded (in
    // declaration order) before descending into any of them. This yields a
    // stable "siblings before descendants" order — e.g. the diamond
    // A -> {B, C} -> D lists [B; C; D] rather than the depth-first [B; D; C].
    let queue = System.Collections.Generic.Queue<string>()
    queue.Enqueue(rootFsprojFull)

    while queue.Count > 0 do
        let key = Path.GetFullPath(queue.Dequeue())

        if visited.Add(key) && File.Exists(key) then
            let content = File.ReadAllText(key)
            let referencingDir = Path.GetDirectoryName(key)

            for incl in parseProjectReferenceIncludes content do
                let normalisedIncl = incl.Replace('\\', '/')
                let refFull = Path.GetFullPath(Path.Combine(referencingDir, normalisedIncl))
                let refDir = toRepoRelativeDir rootDir refFull

                if refDir <> ownDir && seenDirs.Add(refDir) then
                    ordered.Add(refDir)

                queue.Enqueue(refFull)

    List.ofSeq ordered

/// Find all packable fsproj files, returning (packageName, relativePath) list
let findPackableProjects (rootDir: string) : (string * string) list =
    Directory.GetFiles(rootDir, "*.fsproj", SearchOption.AllDirectories)
    |> Array.choose (fun path ->
        let content = File.ReadAllText(path)
        let m = packageIdRegex.Match(content)

        if m.Success && not (isPackableFalseRegex.IsMatch(content)) then
            let relativePath = Path.GetRelativePath(rootDir, path)
            Some(m.Groups[1].Value, relativePath)
        else
            None)
    |> Array.toList

/// Discover a single-package config by finding the packable fsproj
let discover (rootDir: string) : Result<ToolConfig, string> =
    let projects = findPackableProjects rootDir

    match projects.Length with
    | 0 -> Error "No packable .fsproj found (must have <PackageId>)"
    | 1 ->
        let name, relativePath = projects[0]
        let fsproj = Path.Combine(rootDir, relativePath)

        Ok
            { Packages =
                [ { Name = name
                    Fsproj = relativePath
                    DllPath = Path.GetRelativePath(rootDir, deriveDllPath fsproj)
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = rootDir }
    | n -> Error $"Found {n} packable .fsproj files; create a semantic-tagger.json to configure multi-package release"

let private tryGet (name: string) (el: JsonElement) =
    match el.TryGetProperty(name) with
    | true, v -> Some v
    | _ -> None

/// Parse a semantic-tagger.json config string
let parseJson (json: string) : ToolConfig =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let reservedVersions =
        root
        |> tryGet "reservedVersions"
        |> Option.map (fun prop ->
            [ for item in prop.EnumerateArray() do
                  yield item.GetString() ]
            |> Set.ofList)
        |> Option.defaultValue Set.empty

    let packages =
        let pkgs = root.GetProperty("packages")

        [ for pkg in pkgs.EnumerateArray() do
              let name = pkg.GetProperty("name").GetString()
              let fsproj = pkg.GetProperty("fsproj").GetString()

              let tagPrefix =
                  pkg |> tryGet "tagPrefix" |> Option.map _.GetString() |> Option.defaultValue "v"

              let fsProjsSharingSameTag =
                  pkg
                  |> tryGet "fsProjsSharingSameTag"
                  |> Option.map (fun arr ->
                      [ for item in arr.EnumerateArray() do
                            yield item.GetString() ])
                  |> Option.defaultValue []

              let dllPath =
                  pkg
                  |> tryGet "dllPath"
                  |> Option.map _.GetString()
                  |> Option.defaultWith (fun () ->
                      let dir = Path.GetDirectoryName(fsproj)
                      let name = Path.GetFileNameWithoutExtension(fsproj)
                      Path.Combine(dir, "bin", "Release", "net10.0", name + ".dll"))

              yield
                  { Name = name
                    Fsproj = fsproj
                    DllPath = dllPath
                    TagPrefix = tagPrefix
                    FsProjsSharingSameTag = fsProjsSharingSameTag } ]

    let preBuildCmds =
        root
        |> tryGet "preBuildCmds"
        |> Option.map (fun prop ->
            [ for item in prop.EnumerateArray() do
                  yield item.GetString() ])
        |> Option.defaultValue []

    { Packages = packages
      ReservedVersions = reservedVersions
      PreBuildCmds = preBuildCmds
      RootDir = "" }

/// Serialize a ToolConfig to JSON string
let toJson (config: ToolConfig) : string =
    use stream = new System.IO.MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
    writer.WriteStartObject()
    writer.WriteStartArray("packages")

    for pkg in config.Packages do
        writer.WriteStartObject()
        writer.WriteString("name", pkg.Name)
        writer.WriteString("fsproj", pkg.Fsproj)
        writer.WriteString("tagPrefix", pkg.TagPrefix)

        if not pkg.FsProjsSharingSameTag.IsEmpty then
            writer.WriteStartArray("fsProjsSharingSameTag")

            for extra in pkg.FsProjsSharingSameTag do
                writer.WriteStringValue(extra)

            writer.WriteEndArray()

        writer.WriteEndObject()

    writer.WriteEndArray()

    if not config.ReservedVersions.IsEmpty then
        writer.WriteStartArray("reservedVersions")

        for v in config.ReservedVersions do
            writer.WriteStringValue(v)

        writer.WriteEndArray()

    if not config.PreBuildCmds.IsEmpty then
        writer.WriteStartArray("preBuildCmds")

        for cmd in config.PreBuildCmds do
            writer.WriteStringValue(cmd)

        writer.WriteEndArray()

    writer.WriteEndObject()
    writer.Flush()
    System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, int stream.Length)

/// Load config: try semantic-tagger.json first, fall back to discover.
/// DLL paths are always re-derived from the fsproj on disk so that
/// AssemblyName overrides are respected (parseJson can't do I/O).
let load (rootDir: string) : Result<ToolConfig, string> =
    let jsonPath = Path.Combine(rootDir, "semantic-tagger.json")

    if File.Exists(jsonPath) then
        let json = File.ReadAllText(jsonPath)
        let config = parseJson json

        Ok
            { config with
                RootDir = rootDir
                Packages =
                    config.Packages
                    |> List.map (fun pkg ->
                        let fsprojFull = Path.Combine(rootDir, pkg.Fsproj)

                        if File.Exists fsprojFull then
                            { pkg with
                                DllPath = Path.GetRelativePath(rootDir, deriveDllPath fsprojFull) }
                        else
                            pkg) }
    else
        discover rootDir

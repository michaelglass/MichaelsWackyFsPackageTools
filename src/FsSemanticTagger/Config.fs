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

/// A workflow whose run PUBLISHES a package, named by its path under the repo
/// (`.github/workflows/release.yml`). The path is the stable identity: display
/// names are editable and two workflows may share one.
///
/// A distinct type rather than a string so the post-push tag check can only ever
/// be asked about a run from a publishing workflow. A run from
/// any other workflow the tag triggered — a docs deploy cancelled by its own
/// `concurrency` group, say — carries no information about publication and must
/// never refuse a release, and the way to guarantee that is to make such a run
/// unrepresentable in the check rather than to filter it by name inside it.
type PublishWorkflow = PublishWorkflow of path: string

type ToolConfig =
    {
        Packages: PackageConfig list
        ReservedVersions: Set<string>
        PreBuildCmds: string list
        /// The workflows whose run on a pushed tag publishes the package. Read from
        /// `publishWorkflows` in `semantic-tagger.json`; defaults to
        /// `defaultPublishWorkflows`, the release workflow every repo using this
        /// tool ships under the same path.
        PublishWorkflows: PublishWorkflow list
        RootDir: string
    }

/// The publish workflow assumed when `semantic-tagger.json` names none: the
/// tag-triggered release workflow, which lives at this path in every repo this
/// tool releases (this one and FsHotWatch both).
let defaultPublishWorkflows: PublishWorkflow list =
    [ PublishWorkflow ".github/workflows/release.yml" ]

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

let private packAsToolRegex =
    Regex(@"<PackAsTool>\s*true\s*</PackAsTool>", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

let private outputTypeExeRegex =
    Regex(@"<OutputType>\s*Exe\s*</OutputType>", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// True when the fsproj content marks the project `<PackAsTool>true</PackAsTool>`.
/// A pack-as-tool project physically bundles its entire transitive
/// `<ProjectReference>` closure into the published artifact, so a change to any
/// referenced project genuinely changes what ships.
let isPackAsTool (content: string) : bool = packAsToolRegex.IsMatch(content)

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
    projectReferenceIncludeRegex.Matches(content)
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

/// Normalise an arbitrary path to a repo-root-relative directory using forward
/// slashes and no trailing slash. `rootDir` and `absolutePath` are both
/// resolved to full paths first so the relative result is stable.
let private toRepoRelativeDir (rootDir: string) (absolutePath: string) : string =
    let full = Path.GetFullPath(absolutePath)
    let dir = Path.GetDirectoryName(full)

    Path.GetRelativePath(Path.GetFullPath(rootDir), dir).Replace('\\', '/').TrimEnd('/')

/// Normalise an arbitrary fsproj path to a repo-root-relative path using
/// forward slashes (e.g. `src/Foo/Foo.fsproj`). `rootDir` and `absolutePath`
/// are both resolved to full paths first so the relative result is stable. This
/// is the form the `isSeparatelyReleased` predicate of `transitiveBundledRefDirs`
/// receives, so it matches the `Fsproj` values from `semantic-tagger.json`.
let private toRepoRelativeFsproj (rootDir: string) (absolutePath: string) : string =
    let full = Path.GetFullPath(absolutePath)
    Path.GetRelativePath(Path.GetFullPath(rootDir), full).Replace('\\', '/')

/// Walk the `<ProjectReference>` graph breadth-first from `fsprojRelPath`
/// (relative to `rootDir`) and return every reference edge the walk follows, as
/// `(repoRelativeFsproj, absoluteFsproj)` pairs in discovery order (duplicates
/// possible when two projects reference the same one). See
/// `transitiveBundledRefDirs` for the bundling rule and traversal semantics.
let private walkBundledReferences
    (rootDir: string)
    (fsprojRelPath: string)
    (isSeparatelyReleased: string -> bool)
    : (string * string) list =
    let rootFsprojFull = Path.GetFullPath(Path.Combine(rootDir, fsprojRelPath))

    let rootIsTool =
        File.Exists(rootFsprojFull) && isPackAsTool (File.ReadAllText(rootFsprojFull))

    let visited = System.Collections.Generic.HashSet<string>()
    let followed = ResizeArray<string * string>()

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
                let refRel = toRepoRelativeFsproj rootDir refFull

                // A separately-published reference is a NuGet-dependency boundary:
                // skip it and do not recurse past it.
                if rootIsTool || not (isSeparatelyReleased refRel) then
                    followed.Add((refRel, refFull))
                    queue.Enqueue(refFull)

    List.ofSeq followed

/// Resolve the transitive `<ProjectReference>` closure of `fsprojRelPath`
/// (relative to `rootDir`) restricted to the references whose DLL actually
/// *ships inside* the package, returned as repo-root-relative directory paths
/// (forward slashes, no trailing slash).
///
/// `isSeparatelyReleased` is given a referenced project's repo-root-relative
/// fsproj path (forward slashes) and returns true when that project is a
/// separately-published NuGet package — i.e. a dependency boundary that is
/// consumed via a `PackageReference`/`<dependency>` rather than bundled.
///
/// Bundling rule (decided once at the root): if the root fsproj is
/// `<PackAsTool>true</PackAsTool>` it physically ships its ENTIRE transitive
/// closure, so every reference is bundled regardless of the predicate.
/// Otherwise (a library) a referenced project `R` is bundled iff it is NOT
/// separately released: a separately-released `R` is excluded AND not recursed
/// past (its own transitive deps are its concern); a non-published helper `R` is
/// included and recursed through (same rule).
///
/// Each Include is resolved relative to the *referencing* fsproj's directory,
/// then normalised against `rootDir`. Traversal is de-duplicated and cycle-safe
/// (each fsproj is visited at most once). A referenced fsproj that does not
/// exist on disk is skipped silently rather than throwing. The package's *own*
/// directory is excluded — change-detection for the package itself is handled
/// separately by the caller. Results are returned in a stable de-duplicated
/// order (breadth-first discovery order: a project's direct references before
/// their transitive ones).
let transitiveBundledRefDirs
    (rootDir: string)
    (fsprojRelPath: string)
    (isSeparatelyReleased: string -> bool)
    : string list =
    let ownDir =
        toRepoRelativeDir rootDir (Path.GetFullPath(Path.Combine(rootDir, fsprojRelPath)))

    walkBundledReferences rootDir fsprojRelPath isSeparatelyReleased
    |> List.map (fun (_, refFull) -> toRepoRelativeDir rootDir refFull)
    |> List.filter (fun dir -> dir <> ownDir)
    |> List.distinct

/// Resolve the FULL transitive `<ProjectReference>` closure of `fsprojRelPath`
/// (relative to `rootDir`), with no dependency boundaries. Equivalent to
/// `transitiveBundledRefDirs rootDir fsprojRelPath (fun _ -> false)`. See that
/// function for traversal semantics.
let transitiveProjectRefDirs (rootDir: string) (fsprojRelPath: string) : string list =
    transitiveBundledRefDirs rootDir fsprojRelPath (fun _ -> false)

/// The repo-root-relative fsproj path (forward slashes) of `fsprojPath`, which may
/// be relative to `rootDir` or absolute. The identity a `semantic-tagger.json`
/// `fsproj` value and a resolved `<ProjectReference>` are compared by.
let repoRelativeFsproj (rootDir: string) (fsprojPath: string) : string =
    toRepoRelativeFsproj rootDir (Path.Combine(rootDir, fsprojPath))

/// Every fsproj reachable from `fsprojRelPath` through `<ProjectReference>`, with
/// no dependency boundaries, as de-duplicated repo-root-relative fsproj paths in
/// breadth-first discovery order. The starting fsproj itself is not listed, even
/// when a reference cycle leads back to it.
let transitiveProjectRefFsprojs (rootDir: string) (fsprojRelPath: string) : string list =
    let own = repoRelativeFsproj rootDir fsprojRelPath

    walkBundledReferences rootDir fsprojRelPath (fun _ -> false)
    |> List.map fst
    |> List.filter (fun fsproj -> fsproj <> own)
    |> List.distinct

/// Find all packable fsproj files, returning (packageName, relativePath) list.
///
/// A project counts as a release candidate when it has a `<PackageId>`, is not
/// `<IsPackable>false</IsPackable>`, and is not an executable example app: an
/// `<OutputType>Exe</OutputType>` project that lacks `<PackAsTool>true</PackAsTool>`
/// is treated as a runnable example (not something published to NuGet) and
/// excluded. Real dotnet tools (Exe + PackAsTool) and libraries with a PackageId
/// are kept.
let findPackableProjects (rootDir: string) : (string * string) list =
    Directory.GetFiles(rootDir, "*.fsproj", SearchOption.AllDirectories)
    |> Array.choose (fun path ->
        let content = File.ReadAllText(path)
        let m = packageIdRegex.Match(content)

        let isExampleExe =
            outputTypeExeRegex.IsMatch(content) && not (packAsToolRegex.IsMatch(content))

        if m.Success && not (isPackableFalseRegex.IsMatch(content)) && not isExampleExe then
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
              PublishWorkflows = defaultPublishWorkflows
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

    let publishWorkflows =
        root
        |> tryGet "publishWorkflows"
        |> Option.map (fun prop ->
            [ for item in prop.EnumerateArray() do
                  yield PublishWorkflow(item.GetString()) ])
        |> Option.defaultValue defaultPublishWorkflows

    // An empty set would make every tag unconfirmable — no workflow to ask about, so
    // no run can ever appear — and that is a configuration mistake, not a release
    // outcome. Refuse it here, where the operator can read the reason, rather than
    // after the version-bump commit and the tags have gone out.
    if List.isEmpty publishWorkflows then
        invalidArg
            "json"
            "semantic-tagger.json: `publishWorkflows` must name at least one workflow path, or be omitted to default to .github/workflows/release.yml"

    { Packages = packages
      ReservedVersions = reservedVersions
      PreBuildCmds = preBuildCmds
      PublishWorkflows = publishWorkflows
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

    // Written only when it differs from the default, so the file a fresh `init`
    // produces says nothing about a setting the operator has not touched.
    if config.PublishWorkflows <> defaultPublishWorkflows then
        writer.WriteStartArray("publishWorkflows")

        for PublishWorkflow path in config.PublishWorkflows do
            writer.WriteStringValue(path)

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

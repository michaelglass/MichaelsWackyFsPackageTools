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
      PreBuildCmds: string list }

let private assemblyNameRegex =
    Regex(@"<AssemblyName>([^<]+)</AssemblyName>", RegexOptions.Compiled)

let private packageIdRegex =
    Regex(@"<PackageId>([^<]+)</PackageId>", RegexOptions.Compiled)

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
let discover (rootDir: string) : ToolConfig =
    let projects = findPackableProjects rootDir

    match projects.Length with
    | 0 -> failwith "No packable .fsproj found (must have <PackageId>)"
    | 1 ->
        let name, relativePath = projects[0]
        let fsproj = Path.Combine(rootDir, relativePath)

        { Packages =
            [ { Name = name
                Fsproj = relativePath
                DllPath = Path.GetRelativePath(rootDir, deriveDllPath fsproj)
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = [] }
    | n ->
        failwithf "Found %d packable .fsproj files; create a semantic-tagger.json to configure multi-package release" n

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
      PreBuildCmds = preBuildCmds }

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
let load (rootDir: string) : ToolConfig =
    let jsonPath = Path.Combine(rootDir, "semantic-tagger.json")

    if File.Exists(jsonPath) then
        let json = File.ReadAllText(jsonPath)
        let config = parseJson json

        { config with
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

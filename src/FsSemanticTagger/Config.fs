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
      ExtraFsprojs: string list }

type ToolConfig =
    { Packages: PackageConfig list
      ReservedVersions: Set<string> }

let private assemblyNameRegex =
    Regex(@"<AssemblyName>([^<]+)</AssemblyName>", RegexOptions.Compiled)

let private packageIdRegex =
    Regex(@"<PackageId>([^<]+)</PackageId>", RegexOptions.Compiled)

let private isPackableFalseRegex =
    Regex(@"<IsPackable>\s*false\s*</IsPackable>", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

/// Derive the DLL path from an fsproj file path
let deriveDllPath (fsprojPath: string) : string =
    let dir = Path.GetDirectoryName(fsprojPath)
    let content = File.ReadAllText(fsprojPath)
    let assemblyNameMatch = assemblyNameRegex.Match(content)

    let name =
        if assemblyNameMatch.Success then
            assemblyNameMatch.Groups[1].Value
        else
            Path.GetFileNameWithoutExtension(fsprojPath)

    Path.Combine(dir, "bin", "Release", "net10.0", name + ".dll")

/// Discover a single-package config by finding the packable fsproj
let discover (rootDir: string) : ToolConfig =
    let fsprojs =
        Directory.GetFiles(rootDir, "*.fsproj", SearchOption.AllDirectories)
        |> Array.filter (fun path ->
            let content = File.ReadAllText(path)
            let hasPackageId = packageIdRegex.IsMatch(content)
            let isNotPackable = isPackableFalseRegex.IsMatch(content)
            hasPackageId && not isNotPackable)

    match fsprojs.Length with
    | 0 -> failwith "No packable .fsproj found (must have <PackageId>)"
    | 1 ->
        let fsproj = fsprojs[0]
        let relativePath = Path.GetRelativePath(rootDir, fsproj)
        let content = File.ReadAllText(fsproj)
        let packageIdMatch = packageIdRegex.Match(content)
        let name = packageIdMatch.Groups[1].Value

        { Packages =
            [ { Name = name
                Fsproj = relativePath
                DllPath = Path.GetRelativePath(rootDir, deriveDllPath fsproj)
                TagPrefix = "v"
                ExtraFsprojs = [] } ]
          ReservedVersions = Set.empty }
    | n ->
        failwithf "Found %d packable .fsproj files; create a semantic-tagger.json to configure multi-package release" n

/// Parse a semantic-tagger.json config string
let parseJson (json: string) : ToolConfig =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let reservedVersions =
        if root.TryGetProperty("reservedVersions") |> fst then
            let prop = root.GetProperty("reservedVersions")

            [ for item in prop.EnumerateArray() do
                  yield item.GetString() ]
            |> Set.ofList
        else
            Set.empty

    let packages =
        let pkgs = root.GetProperty("packages")

        [ for pkg in pkgs.EnumerateArray() do
              let name = pkg.GetProperty("name").GetString()
              let fsproj = pkg.GetProperty("fsproj").GetString()

              let tagPrefix =
                  if pkg.TryGetProperty("tagPrefix") |> fst then
                      pkg.GetProperty("tagPrefix").GetString()
                  else
                      "v"

              let extraFsprojs =
                  if pkg.TryGetProperty("extraFsprojs") |> fst then
                      [ for item in pkg.GetProperty("extraFsprojs").EnumerateArray() do
                            yield item.GetString() ]
                  else
                      []

              let dllPath =
                  if pkg.TryGetProperty("dllPath") |> fst then
                      pkg.GetProperty("dllPath").GetString()
                  else
                      // Derive from fsproj: read the fsproj to get assembly name
                      let dir = Path.GetDirectoryName(fsproj)

                      let assemblyName = Path.GetFileNameWithoutExtension(fsproj)

                      Path.Combine(dir, "bin", "Release", "net10.0", assemblyName + ".dll")

              yield
                  { Name = name
                    Fsproj = fsproj
                    DllPath = dllPath
                    TagPrefix = tagPrefix
                    ExtraFsprojs = extraFsprojs } ]

    { Packages = packages
      ReservedVersions = reservedVersions }

/// Load config: try semantic-tagger.json first, fall back to discover
let load (rootDir: string) : ToolConfig =
    let jsonPath = Path.Combine(rootDir, "semantic-tagger.json")

    if File.Exists(jsonPath) then
        let json = File.ReadAllText(jsonPath)
        parseJson json
    else
        discover rootDir

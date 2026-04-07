module FsSemanticTagger.Api

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

type ApiSignature = ApiSignature of string

type ApiChange =
    | Breaking of ApiSignature list
    | Addition of ApiSignature list
    | NoChange

/// Build list of paths to search for assembly dependencies
let getAssemblySearchPaths (dllPath: string) : string list =
    let dllDir = Path.GetDirectoryName(Path.GetFullPath(dllPath))
    let runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()

    let sdkDirs =
        let dotnetRoot =
            let envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")

            if not (String.IsNullOrEmpty(envRoot)) then
                envRoot
            else
                let runtimeParent = Path.GetDirectoryName(runtimeDir)
                // runtime dir is like <dotnet>/shared/Microsoft.NETCore.App/10.0.0/
                // go up 3 levels to dotnet root
                Path.GetDirectoryName(Path.GetDirectoryName(runtimeParent))

        let sdkBase = Path.Combine(dotnetRoot, "sdk")

        if Directory.Exists(sdkBase) then
            Directory.GetDirectories(sdkBase)
            |> Array.toList
            |> List.collect (fun sdkDir ->
                let fsharpDir = Path.Combine(sdkDir, "FSharp")
                if Directory.Exists(fsharpDir) then [ fsharpDir ] else [])
        else
            []

    let sharedFrameworkDirs =
        let dotnetRoot =
            let runtimeParent = Path.GetDirectoryName(runtimeDir)
            Path.GetDirectoryName(Path.GetDirectoryName(runtimeParent))

        let sharedBase = Path.Combine(dotnetRoot, "shared")

        if Directory.Exists(sharedBase) then
            Directory.GetDirectories(sharedBase)
            |> Array.toList
            |> List.collect (fun fwDir -> Directory.GetDirectories(fwDir) |> Array.toList)
        else
            []

    let nugetDirs =
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

        let nugetBase = Path.Combine(home, ".nuget", "packages", "fsharp.core")

        if Directory.Exists(nugetBase) then
            Directory.GetDirectories(nugetBase)
            |> Array.toList
            |> List.collect (fun versionDir ->
                let tfms = [ "net10.0"; "net9.0"; "net8.0"; "netstandard2.1"; "netstandard2.0" ]

                tfms
                |> List.map (fun tfm -> Path.Combine(versionDir, "lib", tfm))
                |> List.filter Directory.Exists)
        else
            []

    // Resolve transitive NuGet dependencies from .deps.json
    let depsJsonDirs =
        let assemblyName = Path.GetFileNameWithoutExtension(dllPath)
        let depsJsonPath = Path.Combine(dllDir, assemblyName + ".deps.json")

        if File.Exists(depsJsonPath) then
            let nugetRoot =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")

            try
                use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsJsonPath))
                let root = doc.RootElement

                match root.TryGetProperty("libraries") with
                | true, libs ->
                    libs.EnumerateObject()
                    |> Seq.toList
                    |> List.choose (fun lib ->
                        match lib.Value.TryGetProperty("type"), lib.Value.TryGetProperty("path") with
                        | (true, t), (true, p) when t.GetString() = "package" ->
                            let pkgDir = Path.Combine(nugetRoot, p.GetString())

                            if Directory.Exists(pkgDir) then
                                // Search common lib TFM directories
                                [ "net10.0"; "net9.0"; "net8.0"; "netstandard2.1"; "netstandard2.0" ]
                                |> List.map (fun tfm -> Path.Combine(pkgDir, "lib", tfm))
                                |> List.tryFind Directory.Exists
                            else
                                None
                        | _ -> None)
                | false, _ -> []
            with _ ->
                []
        else
            []

    [ dllDir; runtimeDir ]
    @ sdkDirs
    @ sharedFrameworkDirs
    @ nugetDirs
    @ depsJsonDirs

/// Create MetadataAssemblyResolver using search paths
let createResolver (dllPath: string) : MetadataAssemblyResolver =
    let searchPaths = getAssemblySearchPaths dllPath

    let allDlls =
        searchPaths
        |> List.collect (fun dir ->
            if Directory.Exists(dir) then
                Directory.GetFiles(dir, "*.dll") |> Array.toList
            else
                [])

    // Deduplicate by filename, keeping first occurrence (search paths are priority-ordered)
    let seen =
        System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let uniqueDlls =
        allDlls
        |> List.filter (fun path ->
            let name = Path.GetFileName(path)
            seen.Add(name))

    PathAssemblyResolver(uniqueDlls)

/// Format type name, handling generics and arrays
let rec formatTypeName (t: Type) : string =
    if t.IsArray then
        formatTypeName (t.GetElementType()) + "[]"
    elif t.IsGenericType then
        let baseName = t.Name.Substring(0, t.Name.IndexOf('`'))

        let args = t.GetGenericArguments() |> Array.map formatTypeName |> String.concat ", "

        sprintf "%s<%s>" baseName args
    else
        t.Name

/// Extract all public API signatures from a compiled DLL
let extractFromAssembly (dllPath: string) : ApiSignature list =
    let resolver = createResolver dllPath
    use context = new MetadataLoadContext(resolver)

    let assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath))

    [ for t in assembly.GetExportedTypes() do
          yield ApiSignature(sprintf "type %s" t.FullName)

          // Methods (public, instance + static, declared only)
          for m in
              t.GetMethods(
                  BindingFlags.Public
                  ||| BindingFlags.Instance
                  ||| BindingFlags.Static
                  ||| BindingFlags.DeclaredOnly
              ) do
              if not m.IsSpecialName then
                  let ps =
                      m.GetParameters()
                      |> Array.map (fun p -> formatTypeName p.ParameterType)
                      |> String.concat ", "

                  yield ApiSignature(sprintf "  %s::%s(%s): %s" t.Name m.Name ps (formatTypeName m.ReturnType))

          // Properties
          for p in
              t.GetProperties(
                  BindingFlags.Public
                  ||| BindingFlags.Instance
                  ||| BindingFlags.Static
                  ||| BindingFlags.DeclaredOnly
              ) do
              yield ApiSignature(sprintf "  %s::%s: %s" t.Name p.Name (formatTypeName p.PropertyType))

          // Constructors
          for c in t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) do
              let ps =
                  c.GetParameters()
                  |> Array.map (fun p -> formatTypeName p.ParameterType)
                  |> String.concat ", "

              yield ApiSignature(sprintf "  %s::.ctor(%s)" t.Name ps) ]
    |> List.sort

/// Compare two API surfaces
let compare (baseline: ApiSignature list) (current: ApiSignature list) : ApiChange =
    let baseSet = Set.ofList baseline
    let currSet = Set.ofList current
    let removed = baseSet - currSet |> Set.toList
    let added = currSet - baseSet |> Set.toList

    if not removed.IsEmpty then Breaking removed
    elif not added.IsEmpty then Addition added
    else NoChange

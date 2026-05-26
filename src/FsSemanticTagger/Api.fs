module FsSemanticTagger.Api

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

type ApiSignature = ApiSignature of string

type ApiChange =
    | Breaking of head: ApiSignature * rest: ApiSignature list
    | Addition of head: ApiSignature * rest: ApiSignature list
    | NoChange

module ApiChange =
    let toList =
        function
        | Breaking(h, t)
        | Addition(h, t) -> h :: t
        | NoChange -> []

let private supportedTfms =
    [ "net10.0"; "net9.0"; "net8.0"; "netstandard2.1"; "netstandard2.0" ]

let private getDotnetRoot (runtimeDir: string) =
    let envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")

    if not (String.IsNullOrEmpty(envRoot)) then
        envRoot
    else
        let runtimeParent = Path.GetDirectoryName(runtimeDir)
        // runtime dir is like <dotnet>/shared/Microsoft.NETCore.App/10.0.0/
        // go up 3 levels to dotnet root
        Path.GetDirectoryName(Path.GetDirectoryName(runtimeParent))

let getAssemblySearchPaths (dllPath: string) : string list =
    let dllDir = Path.GetDirectoryName(Path.GetFullPath(dllPath))
    let runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()
    let dotnetRoot = getDotnetRoot runtimeDir

    let sdkDirs =
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
                let tfms = supportedTfms

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
                                supportedTfms
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

/// Try to extract API signatures from a previously published NuGet package
/// under an arbitrary cache root (cacheRoot/<id>/<version>/{lib,tools}/<tfm>/).
let extractFromCacheRoot (cacheRoot: string) (packageId: string) (version: string) : ApiSignature list option =
    let pkgDir = Path.Combine(cacheRoot, packageId.ToLowerInvariant(), version)

    if not (Directory.Exists(pkgDir)) then
        None
    else
        let dllName = packageId + ".dll"

        // Search lib/<tfm>/ (libraries) and tools/<tfm>/any/ (dotnet tools)
        let searchDirs =
            [ Path.Combine(pkgDir, "lib"); Path.Combine(pkgDir, "tools") ]
            |> List.filter Directory.Exists
            |> List.collect (fun dir ->
                Directory.GetDirectories(dir)
                |> Array.toList
                |> List.collect (fun tfmDir ->
                    // lib/<tfm>/ has DLLs directly; tools/<tfm>/any/ has them nested
                    [ tfmDir; Path.Combine(tfmDir, "any") ] |> List.filter Directory.Exists))
            |> List.sortDescending

        searchDirs
        |> List.tryPick (fun dir ->
            let dllPath = Path.Combine(dir, dllName)

            if File.Exists(dllPath) then
                Some(extractFromAssembly dllPath)
            else
                None)

/// Try to extract API signatures from a previously published NuGet package
/// in the default user-local cache at ~/.nuget/packages/.
let extractFromNuGetCache (packageId: string) (version: string) : ApiSignature list option =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    extractFromCacheRoot (Path.Combine(home, ".nuget", "packages")) packageId version

/// Build the `dotnet restore` arguments for the probe project. The probe lives
/// in a temp dir, so NuGet would otherwise resolve sources from the temp/global
/// hierarchy and miss the repo's `nuget.config` — pin it with `--configfile`
/// when present so repo-local / private feeds (and their credentials) are honored.
let internal probeRestoreArgs (nugetConfig: string option) (proj: string) : string =
    match nugetConfig with
    | Some cfg -> sprintf "restore \"%s\" --configfile \"%s\"" proj cfg
    | None -> sprintf "restore \"%s\"" proj

/// The repo's nuget.config in the current working directory, if any.
let private currentNuGetConfig () : string option =
    let cwd = Directory.GetCurrentDirectory()

    [ "nuget.config"; "NuGet.config" ]
    |> List.map (fun n -> Path.Combine(cwd, n))
    |> List.tryFind File.Exists

/// Best-effort: pull a published package version into the local NuGet cache by
/// restoring a throwaway project that references it. Used when the previous
/// release isn't already cached (e.g. a clean machine or CI runner). Returns
/// true if the restore command succeeded.
let downloadToCache (run: string -> string -> Shell.CommandResult) (packageId: string) (version: string) : bool =
    let tmpDir =
        Path.Combine(Path.GetTempPath(), "fsst-probe-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let proj = Path.Combine(tmpDir, "probe.csproj")

        File.WriteAllText(
            proj,
            sprintf
                """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="%s" Version="%s" />
  </ItemGroup>
</Project>"""
                packageId
                version
        )

        match run "dotnet" (probeRestoreArgs (currentNuGetConfig ()) proj) with
        | Shell.Success _ -> true
        | Shell.Failure _ -> false
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

/// Extract the previous release's API: try the local NuGet cache first, then
/// fall back to downloading the published package into the cache. Returns None
/// only when the package genuinely can't be obtained (offline, unpublished, or
/// a private feed without credentials) — callers MUST NOT treat None as
/// "no API change", or a breaking release would be mis-versioned as a patch.
let extractPreviousFromNuGet
    (run: string -> string -> Shell.CommandResult)
    (packageId: string)
    (version: string)
    : ApiSignature list option =
    match extractFromNuGetCache packageId version with
    | Some api -> Some api
    | None ->
        if downloadToCache run packageId version then
            extractFromNuGetCache packageId version
        else
            None

/// Compare two API surfaces
let compare (baseline: ApiSignature list) (current: ApiSignature list) : ApiChange =
    let baseSet = Set.ofList baseline
    let currSet = Set.ofList current
    let removed = baseSet - currSet |> Set.toList
    let added = currSet - baseSet |> Set.toList

    // Detect new DU cases: if a new nested type (Parent+Child) is added
    // where the parent type already existed in the baseline, that's a
    // breaking change — consumers with exhaustive pattern matches will break.
    let hasNewDuCase =
        let baseTypeNames =
            baseSet
            |> Set.filter (fun (ApiSignature s) -> s.StartsWith("type "))
            |> Set.map (fun (ApiSignature s) -> s.Substring(5))

        added
        |> List.exists (fun (ApiSignature s) ->
            if s.StartsWith("type ") then
                let typeName = s.Substring(5)

                match typeName.LastIndexOf('+') with
                | -1 -> false
                | i ->
                    let parent = typeName.Substring(0, i)
                    baseTypeNames.Contains(parent)
            else
                false)

    match removed, added with
    | h :: t, _ -> Breaking(h, t)
    | [], _ when hasNewDuCase ->
        match added with
        | h :: t -> Breaking(h, t)
        | [] -> NoChange
    | [], h :: t -> Addition(h, t)
    | [], [] -> NoChange

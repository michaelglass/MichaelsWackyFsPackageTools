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

/// The user-local NuGet package cache root (~/.nuget/packages).
let private nugetCacheRoot () =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")

/// Pick the first existing lib/<tfm> directory for a cached package-version dir.
let internal pickLibDir (pkgVersionDir: string) : string option =
    supportedTfms
    |> List.map (fun tfm -> Path.Combine(pkgVersionDir, "lib", tfm))
    |> List.tryFind Directory.Exists

/// Resolve a package id + version (which may be a NuGet range such as
/// "[5.2.0, )") to its cached version directory, falling back to the highest
/// cached version when the exact one is absent.
let internal resolveCachedPackageDir (cacheRoot: string) (id: string) (versionSpec: string) : string option =
    let idDir = Path.Combine(cacheRoot, id.ToLowerInvariant())

    if not (Directory.Exists idDir) then
        None
    else
        // Strip range brackets/parens; take the lower bound (first comma part).
        let exact = versionSpec.Trim([| '['; ']'; '('; ')'; ' ' |]).Split(',').[0].Trim()

        let exactDir = Path.Combine(idDir, exact)

        if exact <> "" && Directory.Exists exactDir then
            Some exactDir
        else
            Directory.GetDirectories idDir |> Array.sortDescending |> Array.tryHead

/// Read direct dependency (id, versionSpec) pairs from a cached package's .nuspec.
let internal readNuspecDependencies (pkgVersionDir: string) : (string * string) list =
    try
        match Directory.GetFiles(pkgVersionDir, "*.nuspec") |> Array.tryHead with
        | None -> []
        | Some nuspec ->
            let doc = System.Xml.Linq.XDocument.Load(nuspec: string)

            let attr (e: System.Xml.Linq.XElement) (n: string) =
                e.Attributes()
                |> Seq.tryFind (fun a -> a.Name.LocalName = n)
                |> Option.map (fun a -> a.Value)

            doc.Descendants()
            |> Seq.filter (fun e -> e.Name.LocalName = "dependency")
            |> Seq.choose (fun e ->
                match attr e "id" with
                | Some id -> Some(id, defaultArg (attr e "version") "")
                | None -> None)
            |> Seq.toList
    with _ ->
        []

/// Resolve the transitive dependency lib directories for a dll that lives inside
/// the NuGet package cache (where there is no co-located .deps.json — e.g. when
/// diffing against a previously published package). Walks the package's .nuspec
/// dependency graph under `cacheRoot`, adding each resolved package's lib/<tfm>
/// dir. Returns [] when the dll is not under the cache or has no .nuspec.
let internal nuspecClosureDirsFor (cacheRoot: string) (dllPath: string) : string list =
    let fullDll = Path.GetFullPath(dllPath)
    let dllDir = Path.GetDirectoryName(fullDll)

    let underCache =
        fullDll.StartsWith(
            Path.GetFullPath(cacheRoot) + string Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        )

    let rec findPkgDir (dir: string) =
        if String.IsNullOrEmpty dir then
            None
        elif Directory.Exists dir && Directory.GetFiles(dir, "*.nuspec").Length > 0 then
            Some dir
        else
            findPkgDir (Path.GetDirectoryName dir)

    if not underCache then
        []
    else
        match findPkgDir dllDir with
        | None -> []
        | Some rootPkgDir ->
            let visited =
                System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

            let dirs = System.Collections.Generic.List<string>()

            let rec walk (pkgVersionDir: string) =
                if visited.Add(pkgVersionDir) then
                    for id, ver in readNuspecDependencies pkgVersionDir do
                        match resolveCachedPackageDir cacheRoot id ver with
                        | Some depVerDir ->
                            pickLibDir depVerDir |> Option.iter dirs.Add
                            walk depVerDir
                        | None -> ()

            walk rootPkgDir
            List.ofSeq dirs

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

    // A dll inside the NuGet package cache has no co-located .deps.json, so its
    // transitive dependencies come from the .nuspec graph instead.
    let nuspecClosureDirs = nuspecClosureDirsFor (nugetCacheRoot ()) dllPath

    [ dllDir; runtimeDir ]
    @ sdkDirs
    @ sharedFrameworkDirs
    @ nugetDirs
    @ depsJsonDirs
    @ nuspecClosureDirs

let createResolver (dllPath: string) : MetadataAssemblyResolver =
    let searchPaths = getAssemblySearchPaths dllPath

    let allDlls =
        searchPaths
        |> List.collect (fun dir ->
            if Directory.Exists(dir) then
                Directory.GetFiles(dir, "*.dll") |> Array.toList
            else
                [])

    // Search paths are priority-ordered, so the first occurrence of a filename wins.
    let seen =
        System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let uniqueDlls =
        allDlls
        |> List.filter (fun path ->
            let name = Path.GetFileName(path)
            seen.Add(name))

    PathAssemblyResolver(uniqueDlls)

/// Render a type as a comparison key, handling generics and arrays. A type is
/// identified by its **assembly name + full name**, not its short name: a member
/// whose parameter/return type keeps the same short name but moves to a *different*
/// assembly (e.g. `RouteStore` moving from `TestPrune.Core` to `Falco`) is a breaking
/// change, and only the qualified key makes that visible to the diff. The assembly
/// *name* is used, never its *version*, so a routine dependency version bump is not
/// mistaken for a major break.
let rec formatTypeName (t: Type) : string =
    // Concrete types have a namespace-qualified FullName; open constructed generics
    // (e.g. `List<'T>` in a generic method signature) do not — fall back to Name.
    let fullOrName (ty: Type) =
        if String.IsNullOrEmpty ty.FullName then
            ty.Name
        else
            ty.FullName

    if t.IsArray then
        formatTypeName (t.GetElementType()) + "[]"
    elif t.IsGenericParameter then
        // A generic parameter (e.g. 'T) has no assembly identity of its own.
        t.Name
    elif t.IsGenericType then
        // Namespace-qualified base name with the `n arity suffix stripped (Split keeps
        // the whole name unchanged when there is no arity marker).
        let baseName = (fullOrName t).Split('`').[0]

        let args = t.GetGenericArguments() |> Array.map formatTypeName |> String.concat ", "

        sprintf "%s<%s> [%s]" baseName args (t.Assembly.GetName().Name)
    else
        sprintf "%s [%s]" (fullOrName t) (t.Assembly.GetName().Name)

let extractFromAssembly (dllPath: string) : ApiSignature list =
    let resolver = createResolver dllPath
    use context = new MetadataLoadContext(resolver)

    let assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath))

    [ for t in assembly.GetExportedTypes() do
          yield ApiSignature(sprintf "type %s" t.FullName)

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

          for p in
              t.GetProperties(
                  BindingFlags.Public
                  ||| BindingFlags.Instance
                  ||| BindingFlags.Static
                  ||| BindingFlags.DeclaredOnly
              ) do
              yield ApiSignature(sprintf "  %s::%s: %s" t.Name p.Name (formatTypeName p.PropertyType))

          for c in t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) do
              let ps =
                  c.GetParameters()
                  |> Array.map (fun p -> formatTypeName p.ParameterType)
                  |> String.concat ", "

              yield ApiSignature(sprintf "  %s::.ctor(%s)" t.Name ps) ]
    |> List.sort

/// Locate the candidate directories (newest-tfm-first) and expected DLL file name
/// for a cached package version. Covers the `lib/<tfm>/` (library) and
/// `tools/<tfm>[/any]/` (dotnet tool) layouts, plus the FSharp.Analyzers.SDK
/// `analyzers/dotnet/fs/` layout. Returns `None` when the package-version
/// directory isn't cached. Shared by the API and grammar cache-extraction paths
/// so both look in exactly the same places.
let internal packageCacheSearch
    (cacheRoot: string)
    (packageId: string)
    (version: string)
    : (string list * string) option =
    let pkgDir = Path.Combine(cacheRoot, packageId.ToLowerInvariant(), version)

    if not (Directory.Exists(pkgDir)) then
        None
    else
        let dllName = packageId + ".dll"

        let libToolDirs =
            [ Path.Combine(pkgDir, "lib"); Path.Combine(pkgDir, "tools") ]
            |> List.filter Directory.Exists
            |> List.collect (fun dir ->
                Directory.GetDirectories(dir)
                |> Array.toList
                |> List.collect (fun tfmDir ->
                    // lib/<tfm>/ has DLLs directly; tools/<tfm>/any/ has them nested
                    [ tfmDir; Path.Combine(tfmDir, "any") ] |> List.filter Directory.Exists))

        // An FSharp.Analyzers.SDK analyzer package (IncludeBuildOutput=false,
        // DevelopmentDependency=true) ships its assembly under
        // analyzers/dotnet/fs/<id>.dll with NO lib/, so without this the DLL is
        // never found and the package reads as an orphan tag (AbsentOnFeed).
        // Recurse so any nesting (fs/cs, or a TFM sub-folder) is covered.
        let analyzerDirs =
            let analyzersRoot = Path.Combine(pkgDir, "analyzers")

            if Directory.Exists analyzersRoot then
                Directory.GetDirectories(analyzersRoot, "*", SearchOption.AllDirectories)
                |> Array.toList
            else
                []

        Some(libToolDirs @ analyzerDirs |> List.sortDescending, dllName)

/// Try to extract API signatures from a previously published NuGet package
/// under an arbitrary cache root (cacheRoot/<id>/<version>/{lib,tools}/<tfm>/).
let extractFromCacheRoot (cacheRoot: string) (packageId: string) (version: string) : ApiSignature list option =
    match packageCacheSearch cacheRoot packageId version with
    | None -> None
    | Some(searchDirs, dllName) ->
        searchDirs
        |> List.tryPick (fun dir ->
            let dllPath = Path.Combine(dir, dllName)

            if File.Exists(dllPath) then
                // Degrade gracefully: if a transitive dependency can't be
                // resolved we return None (callers treat that as "couldn't read
                // the previous API" and refuse to guess) rather than crashing.
                try
                    Some(extractFromAssembly dllPath)
                with ex ->
                    eprintfn "Warning: could not read API from %s: %s" dllPath ex.Message
                    None
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

/// Append `--no-http-cache` to the probe restore args so a version published
/// seconds ago isn't masked by NuGet's HTTP cache. Used by the post-release
/// availability poll (a freshly pushed package must be seen as soon as it
/// indexes, not after the HTTP cache expires).
let internal probeAvailabilityArgs (nugetConfig: string option) (proj: string) : string =
    probeRestoreArgs nugetConfig proj + " --no-http-cache"

/// The repo's nuget.config in the current working directory, if any.
let private currentNuGetConfig () : string option =
    let cwd = Directory.GetCurrentDirectory()

    [ "nuget.config"; "NuGet.config" ]
    |> List.map (fun n -> Path.Combine(cwd, n))
    |> List.tryFind File.Exists

/// Create a throwaway probe project in a temp dir that references
/// `packageId`/`version`, run `f` against the project path, and clean up the
/// temp dir afterwards. Shared by the cache-download and availability-probe
/// paths so both reference the same package the same way.
let private withProbeProject (packageId: string) (version: string) (f: string -> 'a) : 'a =
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

        f proj
    finally
        try
            Directory.Delete(tmpDir, true)
        with _ ->
            ()

/// Why a previous-release package couldn't be turned into an API surface, kept
/// distinct so callers can react differently. `Found` carries the API. The two
/// failure cases differ in blame: `AbsentOnFeed` means the feed answered and the
/// package/version genuinely isn't there (an orphan tag whose CI publish never
/// landed — safe to skip and diff against an older published release);
/// `FetchError` is a transient/network/auth fault where the truth is unknown
/// (callers MUST abort rather than guess the bump).
type PreviousApiResult =
    | Found of ApiSignature list
    | AbsentOnFeed
    | FetchError of string

/// Classify a `dotnet restore` failure message into "the package genuinely isn't
/// on the feed" vs "we couldn't reach the feed to find out". NuGet emits NU1101
/// (package not found) / NU1102 (version not found) and the offline/source code
/// paths say "Unable to find package" — those are AbsentOnFeed. Anything else
/// (HTTP errors, "Unable to load the service index ... 404 (Not Found)",
/// connection timeouts, auth failures) is a FetchError we must not treat as
/// absence. We deliberately do NOT match a bare "not found": NU1301 wraps a feed
/// outage as "...404 (Not Found)", and treating that as absence would walk past a
/// genuinely published prior — the exact under-bump the FetchError arm prevents.
let internal classifyRestoreFailure (msg: string) : PreviousApiResult =
    let m = msg.ToLowerInvariant()

    if
        m.Contains("nu1101")
        || m.Contains("nu1102")
        || m.Contains("unable to find package")
    then
        AbsentOnFeed
    else
        FetchError msg

/// Best-effort: pull a published package version into the local NuGet cache by
/// restoring a throwaway project that references it. Used when the previous
/// release isn't already cached (e.g. a clean machine or CI runner). Returns
/// true if the restore command succeeded.
let downloadToCache (run: string -> string -> Shell.CommandResult) (packageId: string) (version: string) : bool =
    withProbeProject packageId version (fun proj ->
        match run "dotnet" (probeRestoreArgs (currentNuGetConfig ()) proj) with
        | Shell.Success _ -> true
        | Shell.Failure _ -> false)

/// The outcome of an HTTP GET, kept THREE-valued because the publication
/// question turns on the distinction a `Result<string, string>` throws away: a
/// definite 404 is *knowledge* (the feed answered; the resource is not there),
/// whereas a timeout, a 5xx, a 401/403 or a DNS failure is the *absence* of
/// knowledge. Collapsing those into one `Error` leaves no way to fail safe.
type HttpResult =
    /// A 2xx response, carrying the body.
    | HttpOk of body: string
    /// The server answered 404: this resource definitively does not exist.
    | HttpNotFound
    /// Everything else — timeout, DNS/connection failure, any non-404 non-2xx
    /// status, auth failure. The truth is UNKNOWN.
    | HttpFailed of reason: string

/// A best-effort HTTP GET seam, injected so feed queries are unit testable
/// without real network. Mirrors the `run` shell seam: a real default is
/// provided, tests pass a fake.
let httpGet (url: string) : HttpResult =
    use client = new System.Net.Http.HttpClient()
    client.Timeout <- System.TimeSpan.FromSeconds(10.0)

    try
        let resp = client.GetAsync(url).GetAwaiter().GetResult()
        let body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        if resp.IsSuccessStatusCode then
            HttpOk body
        elif resp.StatusCode = System.Net.HttpStatusCode.NotFound then
            HttpNotFound
        else
            HttpFailed(sprintf "HTTP %d for %s" (int resp.StatusCode) url)
    with ex ->
        HttpFailed ex.Message

/// The feed's answer to "is this exact package version published?".
///
/// The whole point of the type is that `NotOnFeed` is a POSITIVE claim — the
/// feed was reached and it does not have this version — while `FeedUnknown`
/// admits we could not find out. Only `NotOnFeed` may drive a destructive
/// decision (re-publishing an orphan release); `FeedUnknown` must always behave
/// like `OnFeed`, because the two wrong guesses are not symmetric: a wrong
/// "absent" re-publishes on every run of an outage, while a wrong "published"
/// merely defers to the next run that can reach the feed.
type FeedPresence =
    | OnFeed
    | NotOnFeed
    | FeedUnknown of reason: string

/// The nuget.org v3 flat-container index URL for a package id. The flat container
/// is the lowest-level, fastest-updating publish surface (it's where `restore`
/// downloads the .nupkg from); `index.json` lists every published version of the
/// id under a `"versions"` array, so one cheap GET answers availability for any
/// version of that id. Ids and versions are always lower-cased in these URLs.
let internal flatContainerIndexUrl (packageId: string) : string =
    sprintf "https://api.nuget.org/v3-flatcontainer/%s/index.json" (packageId.ToLowerInvariant())

/// The `versions` array of a flat-container `index.json` body, or `None` when the
/// body cannot be understood (not JSON, or no `versions` array). `None` is
/// deliberately distinct from `Some []`: an unparseable body tells us nothing,
/// while an empty array is the feed positively stating it has no versions.
let internal flatContainerVersions (indexJson: string) : string list option =
    try
        let doc = System.Text.Json.JsonDocument.Parse(indexJson)

        match doc.RootElement.TryGetProperty("versions") with
        | true, versions when versions.ValueKind = System.Text.Json.JsonValueKind.Array ->
            versions.EnumerateArray()
            |> Seq.choose (fun v ->
                if v.ValueKind = System.Text.Json.JsonValueKind.String then
                    Some(v.GetString())
                else
                    None)
            |> List.ofSeq
            |> Some
        | _ -> None
    with _ ->
        None

/// Is `version` present in a flat-container `index.json` body? Matches
/// case-insensitively (the feed stores normalised lower-case versions). A
/// malformed/empty body yields false — callers needing to tell "absent" from
/// "unreadable" apart must use `flatContainerVersions` / `flatContainerPresence`.
let internal flatContainerHasVersion (indexJson: string) (version: string) : bool =
    match flatContainerVersions indexJson with
    | Some versions ->
        versions
        |> List.exists (fun v -> String.Equals(v, version, StringComparison.OrdinalIgnoreCase))
    | None -> false

/// What does the nuget.org flat container say about this exact package version?
/// GETs the id's `index.json` via the injected `fetch`. The flat container is the
/// fastest-updating publish surface, so a just-pushed release shows here well
/// before the registration index that `dotnet restore` resolves against.
///
/// Two answers are DEFINITE: a 200 whose `versions` array lacks the version (the
/// feed enumerated everything it has), and a 404 for the id — the flat
/// container's normal way of saying "no such package", not an error condition.
///
/// Everything else is `FeedUnknown` and must never drive a republish: an
/// unparseable 200 body (a proxy error page, a truncated read) cannot show a
/// version is absent from a list we could not read, and `HttpFailed` covers
/// timeouts, DNS failures, 5xx and auth failures.
let internal flatContainerPresence (fetch: string -> HttpResult) (packageId: string) (version: string) : FeedPresence =
    match fetch (flatContainerIndexUrl packageId) with
    | HttpNotFound -> NotOnFeed
    | HttpFailed reason -> FeedUnknown reason
    | HttpOk body ->
        match flatContainerVersions body with
        | None -> FeedUnknown(sprintf "unreadable flat-container index for %s" packageId)
        | Some versions ->
            if
                versions
                |> List.exists (fun v -> String.Equals(v, version, StringComparison.OrdinalIgnoreCase))
            then
                OnFeed
            else
                NotOnFeed

/// Is this exact package version live on the nuget.org flat container right now?
/// The two-valued view of `flatContainerPresence`, kept for the availability
/// poll, which only ever needs to know "is it there yet".
let internal isPublishedViaFlatContainer (fetch: string -> HttpResult) (packageId: string) (version: string) : bool =
    flatContainerPresence fetch packageId version = OnFeed

/// Is this exact package version restorable right now? Restores a throwaway
/// project that references `packageId`/`version` with `--no-http-cache`, so a
/// just-published release is reported as available as soon as NuGet indexes it.
/// This honours the repo's `nuget.config`, so it is the authority for private
/// feeds (where the nuget.org flat container can't see the package).
let isPublishedViaRestore (run: string -> string -> Shell.CommandResult) (packageId: string) (version: string) : bool =
    withProbeProject packageId version (fun proj ->
        match run "dotnet" (probeAvailabilityArgs (currentNuGetConfig ()) proj) with
        | Shell.Success _ -> true
        | Shell.Failure _ -> false)

/// Is this exact package version published, as a three-valued verdict? THE
/// publication authority: both the post-push availability poll and the orphan-tag
/// detection ask this one question, so "actually on the feed" has a single
/// definition.
///
/// The nuget.org flat container decides (it is the fast-updating publish target
/// for this tool's users, and the only source that can answer *definitely
/// absent*). The `dotnet restore` probe then acts purely as a MONOTONE UPGRADE:
/// it may raise a verdict to `OnFeed`, and may never lower one.
///
/// That asymmetry is the whole design, and it buys two things at once:
///   * A package published only to a PRIVATE feed is invisible to the public flat
///     container, which would call it `NotOnFeed`. The restore probe honours the
///     repo's `nuget.config`, so a success there corrects the verdict — no
///     spurious republish of a privately-published release.
///   * A restore FAILURE never downgrades a definite answer, because it cannot
///     un-know what the flat container already established. This is what makes the
///     check work for `PackAsTool` packages at all: a `PackageReference` probe of
///     a tool package always fails NU1212, so for a tool the probe is structurally
///     incapable of confirming presence. Under a "failure downgrades" rule every
///     tool would be permanently `FeedUnknown` — exactly the gap that left tools
///     unable to recover from an orphan tag.
let checkFeedPresence
    (fetch: string -> HttpResult)
    (run: string -> string -> Shell.CommandResult)
    (packageId: string)
    (version: string)
    : FeedPresence =
    match flatContainerPresence fetch packageId version with
    | OnFeed -> OnFeed
    | verdict ->
        if isPublishedViaRestore run packageId version then
            OnFeed
        else
            verdict

/// Is this exact package version live right now? The two-valued view of
/// `checkFeedPresence`, for callers that only need "is it there yet" (the
/// post-push availability poll).
let isPublished
    (fetch: string -> HttpResult)
    (run: string -> string -> Shell.CommandResult)
    (packageId: string)
    (version: string)
    : bool =
    checkFeedPresence fetch run packageId version = OnFeed

/// Extract the previous release's API: try the local NuGet cache first, then
/// fall back to downloading the published package into the cache. Distinguishes
/// the orphan-tag case (`AbsentOnFeed` — the feed has no such package/version, so
/// a caller may walk back to an older published release) from a transient
/// `FetchError` (offline, feed unreachable, or a private feed without
/// credentials — callers MUST NOT guess the bump). Callers MUST NOT treat either
/// failure as "no API change", or a breaking release would ship as a patch.
let extractPreviousFromNuGetResult
    (run: string -> string -> Shell.CommandResult)
    (packageId: string)
    (version: string)
    : PreviousApiResult =
    match extractFromNuGetCache packageId version with
    | Some api -> Found api
    | None ->
        // A restore that succeeds but still yields nothing readable is AbsentOnFeed
        // (the version isn't materialisable), not a FetchError.
        withProbeProject packageId version (fun proj ->
            match run "dotnet" (probeRestoreArgs (currentNuGetConfig ()) proj) with
            | Shell.Failure(msg, _) -> classifyRestoreFailure msg
            | Shell.Success _ ->
                match extractFromNuGetCache packageId version with
                | Some api -> Found api
                | None -> AbsentOnFeed)

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
    match extractPreviousFromNuGetResult run packageId version with
    | Found api -> Some api
    | AbsentOnFeed
    | FetchError _ -> None

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

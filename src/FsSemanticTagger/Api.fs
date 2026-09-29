module FsSemanticTagger.Api

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

/// One declaration of a public API surface, or a marker standing in for one in a
/// verdict no declaration produced. Each declaration keeps the names it is made of
/// apart, so classifying one never re-parses its rendered text.
[<RequireQualifiedAccess>]
type ApiSignature =
    /// A public case of a union, which consumers can match on.
    | UnionCase of union: string * case: string
    /// A public type, by its full name.
    | TypeDecl of fullName: string
    /// A public method, property or constructor of a type. The type is named by its
    /// short `Name`; `signature` is the member's name with its parameter and return
    /// types, as `ApiSignature.render` prints it after the `::`.
    | Member of declaringType: string * signature: string
    /// Not a declaration: the human-readable reason a verdict carries when no
    /// declaration produced it, such as a CLI grammar change or a changelog's
    /// declared bump.
    | Marker of text: string

module ApiSignature =
    /// The one-line text of a signature, as `extract-api`, `check-api` and the
    /// release output print it.
    let render (signature: ApiSignature) : string =
        match signature with
        | ApiSignature.UnionCase(union, case) -> sprintf "case %s::%s" union case
        | ApiSignature.TypeDecl fullName -> sprintf "type %s" fullName
        | ApiSignature.Member(declaringType, signature) -> sprintf "  %s::%s" declaringType signature
        | ApiSignature.Marker text -> text

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

    /// The verdict in words, naming the signature that decided it.
    let describe (change: ApiChange) : string =
        match change with
        | Breaking(s, _) -> sprintf "a breaking change (%s)" ((ApiSignature.render s).Trim())
        | Addition(s, _) -> sprintf "an addition (%s)" ((ApiSignature.render s).Trim())
        | NoChange -> "no public API change"

let private supportedTfms =
    [ "net10.0"; "net9.0"; "net8.0"; "netstandard2.1"; "netstandard2.0" ]

/// The user-local NuGet package cache root (~/.nuget/packages).
let internal nugetCacheRoot () =
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

/// The lib/<tfm> dirs of the packages the package at `pkgVersionDir` depends on,
/// transitively, by its .nuspec and the .nuspecs under `cacheRoot` it leads to.
let private dependencyClosureDirs (cacheRoot: string) (pkgVersionDir: string) : string list =
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

    walk pkgVersionDir
    List.ofSeq dirs

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
        | Some rootPkgDir -> dependencyClosureDirs cacheRoot rootPkgDir

/// The assemblies `dllPath` references, by name and version, read from its
/// metadata without loading it; none when it cannot be read.
let internal referencedAssemblies (dllPath: string) : (string * Version) list =
    try
        use stream = File.OpenRead dllPath
        use pe = new PortableExecutable.PEReader(stream)
        let reader = Metadata.PEReaderExtensions.GetMetadataReader pe

        [
            for handle in reader.AssemblyReferences do
                let reference = reader.GetAssemblyReference handle
                reader.GetString reference.Name, reference.Version
        ]
    with _ ->
        []

/// The cached version directory of the package `name`, for an assembly `name` at
/// `version`: the package version with the assembly version's major.minor.build,
/// else the highest cached one (an assembly version need not match its
/// package's, as FSharp.Core's 10.0.0.0 in package 10.1.x shows).
let internal cachedPackageDirForAssembly (cacheRoot: string) (name: string) (version: Version) : string option =
    let idDir = Path.Combine(cacheRoot, name.ToLowerInvariant())

    if not (Directory.Exists idDir) then
        None
    else
        let versionDirs = Directory.GetDirectories idDir |> Array.sortDescending
        let assemblyVersion = sprintf "%d.%d.%d" version.Major version.Minor version.Build

        versionDirs
        |> Array.tryFind (fun dir -> (Path.GetFileName dir).Split('-').[0] = assemblyVersion)
        |> Option.orElse (Array.tryHead versionDirs)

/// The lib dirs, with their dependency closures, of the cached packages that
/// ship the assemblies `dllPath` references but `isProvided` says nothing else
/// supplies. This is how the dependencies of a package that declares none are
/// found: an FSharp.Analyzers.SDK analyzer ships under analyzers/dotnet/fs/ with
/// its SDK as a private asset, so its .nuspec lists no dependency, yet its
/// assembly references FSharp.Analyzers.SDK, whose own .nuspec leads on to
/// FSharp.Compiler.Service and FSharp.Core.
let internal referencedPackageDirsFor (cacheRoot: string) (isProvided: string -> bool) (dllPath: string) : string list =
    referencedAssemblies dllPath
    |> List.filter (fun (name, _) -> not (isProvided name))
    |> List.collect (fun (name, version) ->
        match cachedPackageDirForAssembly cacheRoot name version with
        | Some pkgDir -> Option.toList (pickLibDir pkgDir) @ dependencyClosureDirs cacheRoot pkgDir
        | None -> [])

let private getDotnetRoot (dotnetRootVar: string option) (runtimeDir: string) =
    match dotnetRootVar with
    | Some envRoot when envRoot <> "" -> envRoot
    | _ ->
        let runtimeParent = Path.GetDirectoryName(runtimeDir)
        // runtime dir is like <dotnet>/shared/Microsoft.NETCore.App/10.0.0/
        // go up 3 levels to dotnet root
        Path.GetDirectoryName(Path.GetDirectoryName(runtimeParent))

/// Memoizes `compute` per key for the life of the process. `Lazy` makes concurrent
/// first callers share one computation instead of each doing it. A computation
/// that throws is forgotten, so the next caller tries again.
let internal oncePerProcess (compute: string -> 'T) : string -> 'T =
    let cache =
        System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<'T>>(StringComparer.Ordinal)

    fun key ->
        let entry = cache.GetOrAdd(key, (fun k -> lazy (compute k)))

        try
            entry.Value
        with _ ->
            cache.TryRemove(Collections.Generic.KeyValuePair(key, entry)) |> ignore
            reraise ()

/// The directories of the .NET installation at `dotnetRoot` that hold reference
/// assemblies: every SDK's FSharp dir, then every version of every shared framework.
/// The installation does not change while this process runs, so each root is
/// listed once.
let private installationDirsUnder =
    oncePerProcess (fun (dotnetRoot: string) ->
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

        sdkDirs @ sharedFrameworkDirs)

/// The running runtime's directory, then the installation dirs under DOTNET_ROOT
/// (`dotnetRootVar`, else the root the runtime directory sits in).
let private installationDirsFor (dotnetRootVar: string option) : string list =
    let runtimeDir = RuntimeEnvironment.GetRuntimeDirectory()
    runtimeDir :: installationDirsUnder (getDotnetRoot dotnetRootVar runtimeDir)

/// This process's DOTNET_ROOT, if set.
let private dotnetRootFromEnvironment () : string option =
    Option.ofObj (Environment.GetEnvironmentVariable "DOTNET_ROOT")

/// Where a resolver for a dll looks for the assemblies it references, in
/// priority order: the dll's own directory, the .NET installation, then the
/// package directories. The installation does not change while this process
/// runs; a restore can add a package directory mid-run.
type internal AssemblySearchPaths =
    {
        DllDir: string
        Installation: string list
        Packages: string list
    }

    /// Every directory, in priority order.
    member this.All = this.DllDir :: this.Installation @ this.Packages

/// `getAssemblySearchPaths` with the DOTNET_ROOT value passed in rather than read
/// from this process's environment, so a test can vary it without changing the
/// environment every concurrently started `dotnet` inherits.
let internal assemblySearchPathsFor (dotnetRootVar: string option) (dllPath: string) : AssemblySearchPaths =
    let dllDir = Path.GetDirectoryName(Path.GetFullPath(dllPath))

    let nugetDirs =
        let nugetBase = Path.Combine(nugetCacheRoot (), "fsharp.core")

        if Directory.Exists(nugetBase) then
            Directory.GetDirectories(nugetBase)
            |> Array.toList
            |> List.collect (fun versionDir ->
                supportedTfms
                |> List.map (fun tfm -> Path.Combine(versionDir, "lib", tfm))
                |> List.filter Directory.Exists)
        else
            []

    let depsJsonDirs =
        let assemblyName = Path.GetFileNameWithoutExtension(dllPath)
        let depsJsonPath = Path.Combine(dllDir, assemblyName + ".deps.json")

        if File.Exists(depsJsonPath) then
            let nugetRoot = nugetCacheRoot ()

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
    let installation = installationDirsFor dotnetRootVar

    // Last, for a package that declares no dependencies: the cached packages of
    // the assemblies it references that neither its own directory nor the .NET
    // installation supplies.
    let referencedPackageDirs =
        let isProvided (name: string) =
            dllDir :: installation
            |> List.exists (fun dir -> File.Exists(Path.Combine(dir, name + ".dll")))

        referencedPackageDirsFor (nugetCacheRoot ()) isProvided dllPath

    {
        DllDir = dllDir
        Installation = installation
        Packages = nugetDirs @ depsJsonDirs @ nuspecClosureDirs @ referencedPackageDirs
    }

let getAssemblySearchPaths (dllPath: string) : string list =
    (assemblySearchPathsFor (dotnetRootFromEnvironment ()) dllPath).All

/// The DLLs directly in `dir`, or none when it does not exist.
let private dllsIn (dir: string) : string list =
    if Directory.Exists(dir) then
        Directory.GetFiles(dir, "*.dll") |> Array.toList
    else
        []

/// `dllsIn` for a directory of the .NET installation, listed once per process.
/// Listing the installed runtimes is most of the cost of a resolver: thousands of
/// files on a machine with several SDKs and frameworks, which took a GitHub Windows
/// runner about 9s cold. The other directories are listed afresh every time.
let private installedDllsIn = oncePerProcess dllsIn

/// The DLLs a resolver for `dllPath` offers, from the search paths in priority
/// order; the first occurrence of a file name wins.
let internal resolverDllsFor (dotnetRootVar: string option) (dllPath: string) : string list =
    let paths = assemblySearchPathsFor dotnetRootVar dllPath

    let seen =
        System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

    dllsIn paths.DllDir
    @ List.collect installedDllsIn paths.Installation
    @ List.collect dllsIn paths.Packages
    |> List.filter (fun path -> seen.Add(Path.GetFileName(path)))

let createResolver (dllPath: string) : MetadataAssemblyResolver =
    PathAssemblyResolver(resolverDllsFor (dotnetRootFromEnvironment ()) dllPath)

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

/// The attribute of type `attributeFullName` on `m`, read as metadata: this works
/// under a `MetadataLoadContext`, where instantiating the attribute throws.
let internal attributeData (attributeFullName: string) (m: MemberInfo) : CustomAttributeData option =
    m.GetCustomAttributesData()
    |> Seq.tryFind (fun a -> a.AttributeType.FullName = attributeFullName)

/// Does `m` carry an attribute of type `attributeFullName`?
let internal hasAttribute (attributeFullName: string) (m: MemberInfo) : bool =
    attributeData attributeFullName m |> Option.isSome

/// Values of FSharp.Core's `SourceConstructFlags`: construct kinds, the mask that
/// isolates the kind, and the bit a private representation adds above it.
[<Literal>]
let internal SumTypeConstruct = 1

[<Literal>]
let internal RecordTypeConstruct = 2

[<Literal>]
let internal UnionCaseConstruct = 8

[<Literal>]
let internal ConstructKindMask = 31

/// The `SourceConstructFlags` of the `[<CompilationMapping>]` the F# compiler puts
/// on a type or member it compiles from an F# construct, unmasked. A union or
/// record with a private representation carries `NonPublicRepresentation` (32)
/// beside its kind.
let internal compilationFlagOf (m: MemberInfo) : int option =
    attributeData "Microsoft.FSharp.Core.CompilationMappingAttribute" m
    |> Option.bind (fun a ->
        a.ConstructorArguments
        |> Seq.tryFind (fun ca -> ca.ArgumentType.Name = "SourceConstructFlags"))
    |> Option.map (fun ca -> Convert.ToInt32 ca.Value)

/// The union case a public static method constructs, read from the
/// `[<CompilationMapping(SourceConstructFlags.UnionCase, i)>]` the F# compiler puts
/// on each case's factory: `New<Case>` for a case with fields, the `get_<Case>`
/// property getter for a fieldless one. This is the metadata
/// `FSharpType.GetUnionCases` itself reads, so it covers what the compiled shape
/// does not show uniformly: fieldless cases and struct unions have no nested case
/// type, and a module compiles to a class whose nested types look like case
/// types. A union with a private representation has no public factories, so it
/// has no public cases — consumers cannot match on it.
let internal unionCaseOf (m: MethodInfo) : string option =
    let factoryPrefix =
        [ "New"; "get_" ]
        |> List.tryFind (fun prefix -> m.Name.StartsWith(prefix, StringComparison.Ordinal))

    let isCaseFactory () =
        compilationFlagOf m
        |> Option.exists (fun flags -> flags &&& ConstructKindMask = UnionCaseConstruct)

    match factoryPrefix with
    | Some prefix when isCaseFactory () -> Some(m.Name.Substring prefix.Length)
    | _ -> None

/// Characters the compiler puts in the names it invents (`<sumBy>__debug@292`,
/// `<>f__AnonymousType…`, `…$W`) and that no source identifier contains.
let private generatedNameChars = [| '<'; '>'; '@'; '$' |]

/// Did the compiler invent this type or member, rather than the source declare
/// it? It must carry `[<CompilerGenerated>]` AND have a name no source identifier
/// has. Neither test is enough alone: F# also marks the equality and comparison
/// members it derives for records and unions (`Equals`, `CompareTo`,
/// `GetHashCode`) `[<CompilerGenerated>]`, and those are API; while a
/// double-backtick name such as ``` ``user@host`` ``` is declared in source.
/// What this drops: the `__debug` copies of inlined functions a Debug build emits
/// (`Shell::<Bind>__debug@116`), which a Release build does not, and anonymous
/// record types. An anonymous record a public member exposes is still compared,
/// through that member's parameter or return type.
let private isCompilerInvented (m: MemberInfo) : bool =
    m.Name.IndexOfAny generatedNameChars >= 0
    && hasAttribute "System.Runtime.CompilerServices.CompilerGeneratedAttribute" m

/// A compiler-invented type, or one nested inside a compiler-invented type.
let rec private isInventedType (t: Type) : bool =
    isCompilerInvented t
    || (not (isNull t.DeclaringType) && isInventedType t.DeclaringType)

/// The public API signatures of `types`: each type, its public members and
/// constructors, and, for a union, its public cases. Sorted. Types and members
/// the compiler invented are left out (see `isCompilerInvented`), so a Debug and
/// a Release build of one source have the same API.
let extractFromTypes (types: Type seq) : ApiSignature list =
    [
        for t in types |> Seq.filter (isInventedType >> not) do
            yield ApiSignature.TypeDecl t.FullName

            let declaredPublic =
                BindingFlags.Public
                ||| BindingFlags.Instance
                ||| BindingFlags.Static
                ||| BindingFlags.DeclaredOnly

            let properties = t.GetProperties(declaredPublic)

            // A property's accessors are listed as the property below. Other
            // special-name methods are API in their own right: F# marks its
            // operators (`op_Addition`) and active patterns (`|Even|Odd|`)
            // special-name too, as C# does an event's `add_`/`remove_`.
            let propertyAccessors =
                properties
                |> Array.collect (fun p -> p.GetAccessors())
                |> Array.map (fun a -> a.MetadataToken)
                |> Set.ofArray

            for m in t.GetMethods(declaredPublic) do
                if not (propertyAccessors.Contains m.MetadataToken) && not (isCompilerInvented m) then
                    let ps =
                        m.GetParameters()
                        |> Array.map (fun p -> formatTypeName p.ParameterType)
                        |> String.concat ", "

                    yield ApiSignature.Member(t.Name, sprintf "%s(%s): %s" m.Name ps (formatTypeName m.ReturnType))

                // A fieldless case's factory is a property getter, so this is
                // checked for accessors too. Cases are signatures of their own
                // because adding one breaks every consumer's exhaustive match, which
                // no other signature can express.
                if m.IsStatic then
                    match unionCaseOf m with
                    | Some case -> yield ApiSignature.UnionCase(t.FullName, case)
                    | None -> ()

            for p in properties do
                yield ApiSignature.Member(t.Name, sprintf "%s: %s" p.Name (formatTypeName p.PropertyType))

            for c in t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly) do
                let ps =
                    c.GetParameters()
                    |> Array.map (fun p -> formatTypeName p.ParameterType)
                    |> String.concat ", "

                yield ApiSignature.Member(t.Name, sprintf ".ctor(%s)" ps)
    ]
    // A member line names its type by `Name`, not `FullName`, so two same-named
    // types nested in different modules can yield the same line.
    |> List.sortBy ApiSignature.render
    |> List.distinct

/// A DLL read from disk once: its assembly, loaded into a `MetadataLoadContext`
/// whose resolver searches `getAssemblySearchPaths`, and a `PEReader` over the same
/// bytes for what reflection does not show (the IL of method bodies). The API
/// extractor and the CLI grammar reader share one, so a DLL read for both builds
/// its resolver (deps.json, .nuspec closure, referenced packages) and load context
/// once.
[<NoEquality; NoComparison>]
type LoadedDll =
    {
        Path: string
        Assembly: Assembly
        PE: PortableExecutable.PEReader
    }

/// Load `dllPath` and run `read` over it. The load context and the PE reader are
/// disposed when `read` returns, so nothing `read` returns may hold on to them.
let withLoadedDll (dllPath: string) (read: LoadedDll -> 'T) : 'T =
    let image = File.ReadAllBytes dllPath
    use context = new MetadataLoadContext(createResolver dllPath)

    use pe =
        new PortableExecutable.PEReader(Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray image)

    read
        {
            Path = dllPath
            Assembly = context.LoadFromByteArray image
            PE = pe
        }

/// The public API of a loaded DLL.
let extractFromLoaded (dll: LoadedDll) : ApiSignature list =
    extractFromTypes (dll.Assembly.GetExportedTypes())

/// The public API of the DLL at `dllPath`. Throws when it cannot be loaded.
let extractFromAssembly (dllPath: string) : ApiSignature list = withLoadedDll dllPath extractFromLoaded

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
        // never found and the package's API reads as unreadable.
        // Recurse so any nesting (fs/cs, or a TFM sub-folder) is covered.
        let analyzerDirs =
            let analyzersRoot = Path.Combine(pkgDir, "analyzers")

            if Directory.Exists analyzersRoot then
                Directory.GetDirectories(analyzersRoot, "*", SearchOption.AllDirectories)
                |> Array.toList
            else
                []

        Some(libToolDirs @ analyzerDirs |> List.sortDescending, dllName)

/// What the NuGet cache holds for one package version's public API. Kept apart
/// from "is this version published?" on purpose: a package that is in the cache
/// but whose API cannot be read IS a real package — only the feed may say a
/// version is absent.
type CachedApi =
    /// The package version is not in this cache at all.
    | NotCached
    /// The public API was read from the cached assembly.
    | CachedRead of ApiSignature list
    /// The package version IS cached, but no public API could be read from it:
    /// no `<id>.dll` in any searched layout (an MSBuild-only package), or the
    /// assembly failed to load — typically a dependency the load context cannot
    /// resolve. Carries why, naming the assembly and the load error.
    | CachedUnreadable of reason: string

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
let internal currentNuGetConfig () : string option =
    let cwd = Directory.GetCurrentDirectory()

    [ "nuget.config"; "NuGet.config" ]
    |> List.map (fun n -> Path.Combine(cwd, n))
    |> List.tryFind File.Exists

/// Create a throwaway probe project in a temp dir that references
/// `packageId`/`version`, run `f` against the project path, and clean up the
/// temp dir afterwards. Shared by the cache-download and availability-probe
/// paths so both reference the same package the same way.
let internal withProbeProject (packageId: string) (version: string) (f: string -> 'a) : 'a =
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

/// What fetching a previous release's API produced. Deliberately says NOTHING
/// about whether that version is published: the extractor is a proxy for
/// publication with failure modes of its own, and `checkFeedPresence` is the one
/// authority on it. A caller that needs "is it published?" must ask the feed.
type PreviousApiResult =
    /// The prior release's public API.
    | Found of ApiSignature list
    /// The package was obtained (it is in the NuGet cache), but its API could not
    /// be read: it ships no assembly, or the assembly failed to load (an
    /// unresolvable dependency). Carries why. This is NOT evidence that the
    /// version is unpublished — treating it as such was the bug
    /// that diffed a release against an older baseline than the one it follows.
    | Unreadable of reason: string
    /// `dotnet restore` reported the package or version does not exist (NU1101 /
    /// NU1102). Carries the restore output. Whether the version is really absent
    /// is still for the feed to say.
    | NotRestorable of reason: string
    /// A transient/network/auth fault where the truth is unknown (callers MUST
    /// abort rather than guess the bump).
    | FetchError of string

/// Classify a `dotnet restore` failure message into "restore says the package
/// doesn't exist" vs "we couldn't reach the feed to find out". NuGet emits NU1101
/// (package not found) / NU1102 (version not found) and the offline/source code
/// paths say "Unable to find package" — those are NotRestorable. Anything else
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
        NotRestorable msg
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
        // `use`, not `let`: the versions are materialised below, so nothing
        // outlives the document and its pooled buffers go back to the pool.
        use doc = System.Text.Json.JsonDocument.Parse(indexJson)

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
/// The two-valued view of `flatContainerPresence`, for a caller that only needs
/// "is it there yet". No production caller remains: the availability poll moved to
/// the three-valued `checkFeedPresence`, because collapsing "the feed says no" and
/// "the feed did not answer" into `false` is what made a spurious re-release
/// possible. Prefer `flatContainerPresence` for anything new.
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

/// Is this exact package version RESTORABLE right now? The question the gate
/// between publication waves asks, as opposed to `checkFeedPresence`'s "is it
/// published". The two differ by minutes: the flat container lists a version
/// well before a restore of it succeeds (measured on FsHotWatch, four minutes
/// for a CLI), and the next wave's nuspec names this exact version, so a gate
/// that clears on the listing pushes the dependents into that window.
///
/// For a library the `dotnet restore` probe decides, alone. It honours the repo's
/// `nuget.config`, so a privately published package passes without the public
/// index ever listing it, and the index listing a version does not pass a package
/// the probe cannot fetch.
///
/// For a `PackAsTool` package the probe is structurally useless (a
/// `PackageReference` to a tool fails NU1212 whether or not it is published), so
/// the flat container is the strongest answer available and decides instead.
let checkRestorable
    (fetch: string -> HttpResult)
    (run: string -> string -> Shell.CommandResult)
    (isTool: bool)
    (packageId: string)
    (version: string)
    : FeedPresence =
    if isTool then flatContainerPresence fetch packageId version
    elif isPublishedViaRestore run packageId version then OnFeed
    else NotOnFeed

/// Is this exact package version live right now? The two-valued view of
/// `checkFeedPresence`. No production caller remains — the post-push availability
/// poll (`Release.waitForNuGet`) consumes `FeedPresence` directly so that
/// `FeedUnknown` behaves like "published" instead of like "absent". Prefer
/// `checkFeedPresence` for anything new; a `bool` cannot express the distinction a
/// release decision depends on.
let isPublished
    (fetch: string -> HttpResult)
    (run: string -> string -> Shell.CommandResult)
    (packageId: string)
    (version: string)
    : bool =
    checkFeedPresence fetch run packageId version = OnFeed

/// Compare two API surfaces. A removal is breaking; so is a new case on a union
/// that was already public, because consumers' exhaustive matches stop covering
/// it. Every other addition — including new types nested in an existing module,
/// or a brand-new union with its cases — is additive. A `Breaking` result lists
/// only the breaking signatures, cases first, then types, then members, so its
/// head names the declaration a consumer notices.
let compare (baseline: ApiSignature list) (current: ApiSignature list) : ApiChange =
    let baseSet = Set.ofList baseline
    let currSet = Set.ofList current
    let removed = baseSet - currSet |> Set.toList
    let added = currSet - baseSet |> Set.toList |> List.sortBy ApiSignature.render

    let baselineUnions =
        baseline
        |> List.choose (function
            | ApiSignature.UnionCase(union, _) -> Some union
            | ApiSignature.TypeDecl _
            | ApiSignature.Member _
            | ApiSignature.Marker _ -> None)
        |> Set.ofList

    let newCases, additions =
        added
        |> List.partition (function
            | ApiSignature.UnionCase(union, _) -> baselineUnions.Contains union
            | ApiSignature.TypeDecl _
            | ApiSignature.Member _
            | ApiSignature.Marker _ -> false)

    // Cases (grouped by union), then types, then members.
    let breakingOrder (signature: ApiSignature) =
        let rank =
            match signature with
            | ApiSignature.UnionCase(union, _) -> 0, union
            | ApiSignature.TypeDecl _ -> 1, ""
            | ApiSignature.Member _
            | ApiSignature.Marker _ -> 2, ""

        rank, ApiSignature.render signature

    match removed @ newCases |> List.sortBy breakingOrder, additions with
    | h :: t, _ -> Breaking(h, t)
    | [], h :: t -> Addition(h, t)
    | [], [] -> NoChange

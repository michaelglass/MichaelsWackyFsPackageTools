/// Read a DLL's public API and its CLI grammar through one load. Building a DLL's
/// resolver (its deps.json, its .nuspec dependency closure, the packages of the
/// assemblies it references) and loading it is most of the cost of reading it, so
/// each DLL is loaded once and both readers run over that one load.
module FsSemanticTagger.Extraction

open System.IO
open FsSemanticTagger.Api

/// What reading one DLL yields: its public API, as `'api` describes the outcome,
/// and its CLI grammar.
type DllRead<'api> = { Api: 'api; Grammar: GrammarRead }

/// A built DLL: its public API, or why it could not be read.
type ExtractedDll = DllRead<Result<ApiSignature list, string>>

/// A package version in the NuGet cache, its API as `Api.CachedApi` describes.
type CachedDll = DllRead<CachedApi>

/// A prior release as `readPrevious` fetched it, its API as
/// `Api.PreviousApiResult` describes.
type PreviousDll = DllRead<PreviousApiResult>

/// The first of `reads` whose grammar was read, modelled or not; else the first.
let firstGrammarRead (grammarOf: 'a -> GrammarRead) (reads: 'a seq) : 'a option =
    reads
    |> Seq.tryFind (fun read ->
        match grammarOf read with
        | GrammarUnreadable _ -> false
        | GrammarModelled _
        | GrammarNotModellable _ -> true)
    |> Option.orElseWith (fun () -> Seq.tryHead reads)

/// Read the DLL at `dllPath` once, for both its API and its grammar. A DLL that
/// cannot be loaded at all is unreadable for both, for the same reason.
let readDll (dllPath: string) : ExtractedDll =
    try
        withLoadedDll dllPath (fun dll ->
            {
                Api =
                    try
                        Ok(extractFromLoaded dll)
                    with ex ->
                        Error(couldNotLoad dllPath ex)
                Grammar = Grammar.readLoaded dll
            })
    with ex ->
        let reason = couldNotLoad dllPath ex

        {
            Api = Error reason
            Grammar = GrammarUnreadable reason
        }

/// Read a previously published package from an arbitrary cache root
/// (cacheRoot/<id>/<version>/{lib,tools,analyzers}/..., see
/// `Api.packageCacheSearch`). Its candidate assemblies are tried newest-tfm-first,
/// each loaded at most once: the API comes from the first whose API reads, the
/// grammar from the first whose grammar reads. When none reads, the FIRST failure
/// is reported, since it names the assembly and the dependency that could not be
/// resolved.
let readCacheRoot (cacheRoot: string) (packageId: string) (version: string) : CachedDll =
    match packageCacheSearch cacheRoot packageId version with
    | None ->
        {
            Api = NotCached
            Grammar = GrammarUnreadable(sprintf "%s %s is not in the NuGet cache at %s" packageId version cacheRoot)
        }
    | Some(searchDirs, dllName) ->
        let noAssembly =
            sprintf
                "%s %s is in the NuGet cache but ships no %s under lib/, tools/ or analyzers/"
                packageId
                version
                dllName

        // Each read on first use and remembered, so a candidate both answers need is
        // loaded once and a candidate after the one both answers came from never is.
        let reads =
            searchDirs
            |> List.map (fun dir -> Path.Combine(dir, dllName))
            |> List.filter File.Exists
            |> List.map (fun dllPath -> lazy (readDll dllPath))

        let rec firstApi (reads: Lazy<ExtractedDll> list) (firstFailure: string option) =
            match reads with
            | [] -> CachedUnreadable(defaultArg firstFailure noAssembly)
            | read :: rest ->
                match read.Value.Api with
                | Ok api -> CachedRead api
                | Error reason -> firstApi rest (Some(defaultArg firstFailure reason))

        let api = firstApi reads None

        let grammar =
            reads
            |> firstGrammarRead (fun read -> read.Value.Grammar)
            |> Option.map (fun read -> read.Value.Grammar)
            |> Option.defaultValue (GrammarUnreadable noAssembly)

        { Api = api; Grammar = grammar }

/// `readCacheRoot` over the user-local NuGet cache at ~/.nuget/packages/.
let readNuGetCache (packageId: string) (version: string) : CachedDll =
    readCacheRoot (nugetCacheRoot ()) packageId version

/// Read a prior release: from the local NuGet cache, else by restoring it into the
/// cache first and reading it from there.
///
/// A package that is cached but unreadable is `Unreadable` straight away — the
/// package plainly exists, and restoring it again cannot change its contents.
/// Neither `Unreadable` nor `NotRestorable` claims the version is unpublished;
/// callers decide that with `checkFeedPresence`. A transient `FetchError` (offline,
/// feed unreachable, or a private feed without credentials) means callers MUST
/// NOT guess the bump. Callers MUST NOT treat any failure as "no API change", or a
/// breaking release would ship as a patch.
///
/// A restore that fails leaves the grammar as the cache read it: unreadable.
let readPrevious (run: string -> string -> Shell.CommandResult) (packageId: string) (version: string) : PreviousDll =
    let asPrevious (api: CachedApi) : PreviousApiResult option =
        match api with
        | CachedRead api -> Some(Found api)
        | CachedUnreadable reason -> Some(Unreadable reason)
        | NotCached -> None

    let cached = readNuGetCache packageId version

    match asPrevious cached.Api with
    | Some api -> { Api = api; Grammar = cached.Grammar }
    | None ->
        withProbeProject packageId version (fun proj ->
            match run "dotnet" (probeRestoreArgs (currentNuGetConfig ()) proj) with
            | Shell.Failure(msg, _) ->
                {
                    Api = classifyRestoreFailure msg
                    Grammar = cached.Grammar
                }
            | Shell.Success _ ->
                let restored = readNuGetCache packageId version

                match asPrevious restored.Api with
                | Some api ->
                    {
                        Api = api
                        Grammar = restored.Grammar
                    }
                | None ->
                    // Restore said yes but the package is not where we read from
                    // (e.g. a relocated global packages folder). The truth is
                    // unknown, so this must abort, never walk back.
                    {
                        Api =
                            FetchError(
                                sprintf
                                    "restore succeeded but %s %s is not in the NuGet cache at ~/.nuget/packages"
                                    packageId
                                    version
                            )
                        Grammar = cached.Grammar
                    })

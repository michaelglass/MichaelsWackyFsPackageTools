/// Stand-ins for the release's DLL readers (`ReleaseInput.ExtractPrevious`,
/// `ExtractCachedPrevious` and `ExtractCurrent`), for tests that fix what a DLL
/// reads as.
module FsSemanticTagger.Tests.ExtractionFakes

open FsSemanticTagger
open FsSemanticTagger.Api
open FsSemanticTagger.Extraction

/// No CommandTree grammar: the API diff alone decides the bump.
let noPreviousGrammar: GrammarRead = GrammarUnreadable "not in the NuGet cache"

/// A prior release whose public API reads as `api` and whose CLI grammar reads as
/// `grammar`.
let previousWith
    (api: string -> string -> PreviousApiResult)
    (grammar: string -> string -> GrammarRead)
    : string -> string -> PreviousDll =
    fun pkg version ->
        {
            Api = api pkg version
            Grammar = grammar pkg version
        }

/// Prior-release stub: a FetchError, so an Auto run that reaches it aborts instead
/// of bumping.
let noPrevious: string -> string -> PreviousDll =
    previousWith (fun _ _ -> FetchError "previous API unavailable") (fun _ _ -> noPreviousGrammar)

/// A prior release the NuGet cache does not hold.
let noCachedPrevious (_pkg: string) (_version: string) : CachedDll =
    {
        Api = NotCached
        Grammar = noPreviousGrammar
    }

/// A prior release the NuGet cache holds, reading as `api` and `grammar`.
let cachedWith (api: CachedApi) (grammar: GrammarRead) (_pkg: string) (_version: string) : CachedDll =
    { Api = api; Grammar = grammar }

/// A prior-release reader for a package that must be read from the cache only: a
/// restore of a PackAsTool package fails NU1212.
let mustNotRestore (_pkg: string) (_version: string) : PreviousDll =
    failwith "a PackAsTool package's previous release must not be restored (NU1212)"

/// A current build whose public API reads as `api` and whose CLI grammar is
/// `grammar`, or none when `grammar` is `None`.
let currentWith (api: string -> ApiSignature list) (grammar: string -> Grammar option) : string -> ExtractedDll =
    fun dll ->
        {
            Api = Ok(api dll)
            Grammar =
                match grammar dll with
                | Some g -> GrammarModelled g
                | None -> GrammarNotModellable "it is not a CommandTree consumer"
        }

/// A current build with no public API and no CLI grammar.
let noCurrent: string -> ExtractedDll = currentWith (fun _ -> []) (fun _ -> None)

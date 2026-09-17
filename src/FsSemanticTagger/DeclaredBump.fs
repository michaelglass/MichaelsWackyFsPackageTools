/// The bump a changelog DECLARES, and how it bounds the bump the API diff computes.
///
/// The API diff cannot see every breaking change. A `[<Literal>]` is inlined into
/// each consumer at compile time and never appears in the public-API dump, so
/// changing one is invisible to the diff while breaking every consumer compiled
/// against the old value. An author who writes `feat!:` in `## Unreleased` has
/// said so; the tagger must not ship that as a patch.
///
/// So a declaration is a FLOOR: the bump is the stronger of the declared and the
/// computed change, and any disagreement is reported rather than silently resolved.
/// Only ever honouring a declaration keeps the tool from inventing a major. Diffing
/// literal values instead was considered and deferred: it would major-bump every
/// package whose internal constants move (build stamps, tuning knobs, thresholds),
/// to catch only the author who forgot to declare.
module FsSemanticTagger.DeclaredBump

open System.Text.RegularExpressions
open FsSemanticTagger.Api

/// The strength a changelog entry declares. Cases are ordered weakest first, so
/// structural comparison ranks them.
type DeclaredLevel =
    /// A recognised conventional type that is neither `feat` nor breaking.
    | DeclaresPatch
    | DeclaresFeature
    /// A `!` marker on any recognised type, or a `BREAKING CHANGE:` note.
    | DeclaresBreaking

type Declaration =
    {
        Level: DeclaredLevel
        /// The changelog that declared it.
        Source: string
        /// The entry line that declared it, trimmed.
        Entry: string
    }

/// A leading list marker (`-`, `*`, `+`) and a leading bold/emphasis opener, so a
/// `- **feat!:** ...` entry is read as the `feat!:` it is.
let private entryPrefixRegex =
    Regex(@"^(?:[-*+]\s+)?(?:\*\*|__)?", RegexOptions.Compiled)

/// The conventional-commit breaking-change footer token. Upper case by the spec,
/// so prose like "Breaking change: ..." is not read as one.
let private breakingNoteRegex =
    Regex(@"^BREAKING[ -]CHANGE:", RegexOptions.Compiled)

/// What one changelog line declares. Only a marker at the START of the entry
/// counts, with the same conventional types `Changelog` groups bullets by; a marker
/// mentioned mid-sentence, or on an unrecognised type, declares nothing.
let markerLevel (line: string) : DeclaredLevel option =
    let entry = entryPrefixRegex.Replace(line.Trim(), "", 1)

    if breakingNoteRegex.IsMatch entry then
        Some DeclaresBreaking
    else
        let m = Changelog.conventionalPrefixRegex.Match entry
        let ty = m.Groups[1].Value.ToLowerInvariant()

        if not (m.Success && Changelog.conventionalTypes.Contains ty) then
            None
        elif m.Groups[3].Success then
            Some DeclaresBreaking
        elif ty = "feat" then
            Some DeclaresFeature
        else
            Some DeclaresPatch

/// The strongest declaration, keeping the first on a tie.
let strongest (declarations: Declaration list) : Declaration option =
    (None, declarations)
    ||> List.fold (fun best d ->
        match best with
        | Some b when b.Level >= d.Level -> best
        | _ -> Some d)

/// The strongest declaration among `lines` of the changelog at `source`. Lines in
/// fenced code are sample text, not entries, and declare nothing.
let declare (source: string) (lines: string list) : Declaration option =
    lines
    |> List.fold
        (fun (inFence, found) line ->
            let t = (line: string).TrimStart()

            if t.StartsWith("```") || t.StartsWith("~~~") then
                (not inFence, found)
            elif inFence then
                (inFence, found)
            else
                let found =
                    match markerLevel line with
                    | Some level ->
                        { Level = level
                          Source = source
                          Entry = line.Trim() }
                        :: found
                    | None -> found

                (inFence, found))
        (false, [])
    |> snd
    |> List.rev
    |> strongest

let private rank (change: ApiChange) : DeclaredLevel =
    match change with
    | Breaking _ -> DeclaresBreaking
    | Addition _ -> DeclaresFeature
    | NoChange -> DeclaresPatch

let private describeLevel (level: DeclaredLevel) : string =
    match level with
    | DeclaresBreaking -> "a breaking change"
    | DeclaresFeature -> "a feature"
    | DeclaresPatch -> "a patch-level change"

let private describeChange (change: ApiChange) : string =
    match change with
    | Breaking(ApiSignature s, _) -> sprintf "a breaking change (%s)" (s.Trim())
    | Addition(ApiSignature s, _) -> sprintf "an addition (%s)" (s.Trim())
    | NoChange -> "no public API change"

/// Bound `computed` below by `declared`: the result is the stronger of the two.
/// The report is `Some` exactly when they disagree, in either direction — a
/// declaration raising the bump says it did and why; a computed change stronger
/// than declared is harmless to the version but means the changelog undersells the
/// release, so it is said out loud too. Nothing declared: `computed`, silently.
let floor (computed: ApiChange) (declared: Declaration option) : ApiChange * string option =
    match declared with
    | None -> computed, None
    | Some d when d.Level > rank computed ->
        let marker =
            ApiSignature(sprintf "changelog: %s declares %s" d.Source (describeLevel d.Level))

        let change =
            match d.Level with
            | DeclaresBreaking -> Breaking(marker, [])
            | _ -> Addition(marker, [])

        change,
        Some(
            sprintf
                "the changelog declares %s, but the API diff found %s. Bumping as %s: a declared change is a floor the API diff cannot lower. Declared in %s by: %s"
                (describeLevel d.Level)
                (describeChange computed)
                (describeLevel d.Level)
                d.Source
                d.Entry
        )
    | Some d when d.Level < rank computed ->
        computed,
        Some(
            sprintf
                "the API diff found %s, but the changelog declares only %s (strongest entry, in %s: %s). Bumping from the API diff, the stronger of the two; if the change is intended, declare it in the changelog."
                (describeChange computed)
                (describeLevel d.Level)
                d.Source
                d.Entry
        )
    | Some _ -> computed, None

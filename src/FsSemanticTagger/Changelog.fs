module FsSemanticTagger.Changelog

open System
open System.IO
open FsSemanticTagger.Version

type ChangelogError =
    | NoFile of path: string
    | NoUnreleasedSection of path: string
    | EmptyUnreleasedSection of path: string
    | CalloutNotFirst of path: string * title: string * line: int

let formatError (err: ChangelogError) : string =
    match err with
    | NoFile p -> sprintf "%s: CHANGELOG.md not found" p
    | NoUnreleasedSection p -> sprintf "%s: no '## Unreleased' section" p
    | EmptyUnreleasedSection p -> sprintf "%s: '## Unreleased' section is empty" p
    | CalloutNotFirst(p, title, line) ->
        String.concat
            "\n"
            [ sprintf "%s: the '## Unreleased' callout is buried — it is not the first thing in the section." p
              sprintf "    Callout: \"%s\" (line %d)." title line
              "    A callout — a blockquote opening with a heading ('> ### ...') or an alert ('> [!WARNING]') —"
              "    exists to be read FIRST, so it must be the first content under '## Unreleased'."
              "    Fix: move the whole '> ...' block back to directly under the '## Unreleased' heading, above"
              "    every entry. The usual cause is a merge that prepended its entries above it."
              "    If this blockquote is not a callout, drop its leading heading or alert marker — a plain"
              "    '> quote' is ignored by this check." ]

let internal isUnreleasedHeading (line: string) : bool =
    let trimmed = line.TrimEnd()

    if not (trimmed.StartsWith("## ")) then
        false
    else
        let rest = trimmed.Substring(3).Trim()

        let stripped =
            if rest.StartsWith("[") && rest.EndsWith("]") then
                rest.Substring(1, rest.Length - 2).Trim()
            else
                rest

        String.Equals(stripped, "Unreleased", StringComparison.OrdinalIgnoreCase)

let private isLevel2Heading (line: string) : bool = line.TrimStart().StartsWith("## ")

/// The body of the `## Unreleased` section as (0-based file line index, text)
/// pairs: every line after the heading up to the next `## ` heading, or EOF.
/// `None` when the file has no `## Unreleased` heading at all.
let private unreleasedBody (lines: string[]) : (int * string)[] option =
    match lines |> Array.tryFindIndex isUnreleasedHeading with
    | None -> None
    | Some idx ->
        lines
        |> Array.indexed
        |> Array.skip (idx + 1)
        |> Array.takeWhile (fun (_, l) -> not (isLevel2Heading l))
        |> Some

let validateUnreleased (changelogPath: string) : Result<unit, ChangelogError> =
    if not (File.Exists changelogPath) then
        Error(NoFile changelogPath)
    else
        match unreleasedBody (File.ReadAllLines changelogPath) with
        | None -> Error(NoUnreleasedSection changelogPath)
        | Some body ->
            if body |> Array.exists (fun (_, l) -> not (String.IsNullOrWhiteSpace l)) then
                Ok()
            else
                Error(EmptyUnreleasedSection changelogPath)

/// Opens a CALLOUT: a blockquote line that is either an ATX heading
/// (`> ### Read this first`) or a GitHub alert marker (`> [!WARNING]`). Those
/// are the two structural shapes of a *titled banner*; a plain `> quoted line`
/// is an ordinary aside and is deliberately not matched. Group `t` is the
/// heading text, group `a` the alert kind.
let private calloutOpenerRegex =
    System.Text.RegularExpressions.Regex(
        @"^>\s*(?:#{1,6}\s+(?<t>\S.*)|\[!(?<a>NOTE|TIP|IMPORTANT|WARNING|CAUTION)\])",
        System.Text.RegularExpressions.RegexOptions.Compiled
        ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )

/// The callout's title, when `line` opens one. Used verbatim in the error so a
/// reader mid-merge sees which block moved.
let internal calloutTitle (line: string) : string option =
    let m = calloutOpenerRegex.Match(line.TrimStart())

    if not m.Success then
        None
    elif m.Groups["t"].Success then
        Some(m.Groups["t"].Value.TrimEnd())
    else
        Some(sprintf "[!%s]" (m.Groups["a"].Value))

/// A fenced-code delimiter. Everything between two of them is sample text, so a
/// `>` in there is markdown being *shown*, not a blockquote in the document.
let private isFenceDelimiter (line: string) : bool =
    let t = line.TrimStart()
    t.StartsWith("```") || t.StartsWith("~~~")

type private SectionScan =
    {
        InFence: bool
        /// Whether the previous line was part of a blockquote, so a `>` line can
        /// be told apart as *opening* a block versus continuing one. Only an
        /// opening line can be a callout — a heading deeper inside one long
        /// callout is part of that same block, not a second one.
        InQuoteBlock: bool
        /// File line index of the section's first non-blank content.
        FirstContent: int option
        /// File line index and title of the first callout in the section.
        Callout: (int * string) option
    }

let private scanSection (body: (int * string)[]) : SectionScan =
    let start =
        { InFence = false
          InQuoteBlock = false
          FirstContent = None
          Callout = None }

    (start, body)
    ||> Array.fold (fun state (i, line) ->
        let firstContent = state.FirstContent |> Option.orElse (Some i)

        if isFenceDelimiter line then
            { state with
                InFence = not state.InFence
                InQuoteBlock = false
                FirstContent = firstContent }
        elif state.InFence then
            state
        elif String.IsNullOrWhiteSpace line then
            { state with InQuoteBlock = false }
        elif line.TrimStart().StartsWith(">") then
            let callout =
                if state.Callout.IsSome || state.InQuoteBlock then
                    state.Callout
                else
                    calloutTitle line |> Option.map (fun t -> i, t)

            { state with
                InQuoteBlock = true
                FirstContent = firstContent
                Callout = callout }
        else
            { state with
                InQuoteBlock = false
                FirstContent = firstContent })

/// A callout — the "read this first" banner — must be the FIRST content of the
/// `## Unreleased` section. Its whole job is to be read before the entries, and
/// a merge that keeps both sides of a conflict happily prepends the incoming
/// entries above it: no conflict marker, no lost text, and a document whose
/// lead block is now buried. That is a structural fact about the section, so it
/// is checked structurally, keying on markdown shape and never on prose.
///
/// Deliberately narrow and deliberately without an escape hatch:
/// - Only a blockquote *opening with a heading or a GitHub alert* counts (see
///   `calloutOpenerRegex`). A plain `> quote` — an aside, some quoted output —
///   is ignored wherever it sits.
/// - Fenced code is skipped, so a changelog that *documents* callout syntax
///   doesn't fail on its own example.
/// - Only the first callout is judged, and only against the section's first
///   content. A second callout further down is that section's business.
/// - Only `## Unreleased`. Released sections are history; re-ordering them is
///   not a live risk and a rule over them would break existing changelogs.
///
/// A missing file or a missing `## Unreleased` section is `Ok` here — those are
/// `validateUnreleased`'s errors, and reporting them twice helps nobody. This is
/// kept OUT of `validateUnreleased` on purpose: that function answers "is there
/// something to promote?", and its callers suppress its error when the section
/// is derivable from commits. A buried callout is not derivable and must never
/// be suppressed, and a section with a buried callout is still promotable.
let validateCalloutOrder (changelogPath: string) : Result<unit, ChangelogError> =
    if not (File.Exists changelogPath) then
        Ok()
    else
        match unreleasedBody (File.ReadAllLines changelogPath) with
        | None -> Ok()
        | Some body ->
            let scan = scanSection body

            match scan.Callout, scan.FirstContent with
            | Some(i, title), Some first when i <> first -> Error(CalloutNotFirst(changelogPath, title, i + 1))
            | _ -> Ok()

/// Conventional-commit types recognised for changelog grouping. An unrecognised
/// prefix (or no prefix) falls into the "other" group and is kept verbatim.
let internal conventionalTypes =
    set
        [ "feat"
          "fix"
          "chore"
          "docs"
          "refactor"
          "perf"
          "test"
          "build"
          "ci"
          "style"
          "revert" ]

/// Matches a conventional-commit prefix at the start of a summary line:
/// `<type>` (letters), an optional `(scope)`, an optional `!` breaking marker,
/// then a colon. Captures the type (1) and the breaking marker (3).
let internal conventionalPrefixRegex =
    System.Text.RegularExpressions.Regex(
        @"^([a-zA-Z]+)(\([^)]*\))?(!)?:",
        System.Text.RegularExpressions.RegexOptions.Compiled
    )

/// The first non-blank line of a (possibly multi-line) commit description,
/// trimmed. `None` when the description is entirely blank.
let private summaryLine (description: string) : string option =
    description.Split('\n')
    |> Array.map (fun l -> l.Trim())
    |> Array.tryFind (fun l -> l <> "")

/// Derive `## Unreleased` changelog bullet lines from a set of commit
/// descriptions. Only each description's SUMMARY line (its first non-blank line)
/// is used — jj descriptions are long and multi-line, and the changelog stays
/// readable with one bullet per commit. Each summary is
/// parsed for a conventional-commit prefix (feat/fix/chore/docs/refactor/perf/
/// test/build/ci/style/revert, an optional (scope), and an optional `!` breaking
/// marker) and the bullets are returned grouped in a stable order: breaking-
/// marked commits first, then feat, then fix, then the remaining recognised
/// types clustered in first-seen order, then un-prefixed commits ("other") last.
/// The tool's own "Bump versions: ..." commits and blank descriptions are
/// dropped, and identical bullets are de-duplicated (a squash/merge can surface
/// the same summary twice). A recognised type keeps its `- <type>: <summary>`
/// form (the `!` retained for breaking); an un-prefixed commit becomes
/// `- <summary>`.
let deriveUnreleasedBullets (descriptions: string list) : string list =
    // (rank, recognised-type-for-clustering). Rank orders the groups:
    //   0 breaking · 1 feat · 2 fix · 3 other recognised · 4 un-prefixed.
    // The type is carried only for rank 3 so those bullets cluster by type in
    // first-seen order; feat/fix/breaking/other each form a single group.
    let classify (summary: string) : int * string option =
        let m = conventionalPrefixRegex.Match(summary)

        if not m.Success then
            (4, None)
        else
            let ty = m.Groups[1].Value.ToLowerInvariant()
            let breaking = m.Groups[3].Success

            if not (conventionalTypes.Contains ty) then (4, None)
            elif breaking then (0, None)
            elif ty = "feat" then (1, None)
            elif ty = "fix" then (2, None)
            else (3, Some ty)

    let entries =
        descriptions
        |> List.choose summaryLine
        // The tool's own version-bump commits are noise, never changelog content.
        |> List.filter (fun s -> not (s.StartsWith("Bump versions:")))
        |> List.mapi (fun i s ->
            let rank, ty = classify s

            {| Index = i
               Rank = rank
               Type = ty
               Bullet = "- " + s |})
        // Keep the first occurrence of each identical bullet.
        |> List.distinctBy (fun r -> r.Bullet)

    // First-seen index of each rank-3 type, so those groups keep a stable order.
    let typeFirstSeen =
        (Map.empty, entries)
        ||> List.fold (fun acc r ->
            match r.Type with
            | Some t when not (acc |> Map.containsKey t) -> acc |> Map.add t r.Index
            | _ -> acc)

    entries
    |> List.sortBy (fun r ->
        let secondary =
            match r.Type with
            | Some t -> typeFirstSeen[t]
            | None -> r.Index

        (r.Rank, secondary, r.Index))
    |> List.map (fun r -> r.Bullet)

let promoteUnreleased (changelogPath: string) (version: Version) (today: DateTime) : unit =
    let lines = File.ReadAllLines changelogPath
    let idx = lines |> Array.findIndex isUnreleasedHeading

    let versionHeader =
        sprintf "## %s - %s" (format version) (today.ToString("yyyy-MM-dd"))

    let before = lines |> Array.take idx
    let after = lines |> Array.skip (idx + 1)

    let freshBlock = [| "## Unreleased"; ""; versionHeader |]
    let rebuilt = Array.concat [ before; freshBlock; after ]

    File.WriteAllLines(changelogPath, rebuilt)

/// Promote `## Unreleased` to a version heading like `promoteUnreleased`, but
/// tolerate a missing or empty `## Unreleased` section: in that case a fresh
/// `## <version> - <date>` heading is inserted (with `defaultBullet` as its only
/// entry) and a new empty `## Unreleased` placed above it. Used for
/// dependency-triggered "rebundle" bumps whose real change lives in a
/// dependency's changelog, so the package's own changelog has nothing to
/// promote. When the section exists with usable content this behaves exactly
/// like `promoteUnreleased` and `defaultBullet` is ignored.
///
/// A missing CHANGELOG.md file is created with a minimal `# Changelog` header.
///
/// `bodyLines` are the changelog entry lines written under the freshly-inserted
/// version heading (each already `- `-prefixed by the caller). `promoteOrInsert`
/// is the single-bullet convenience over this.
let internal promoteOrInsertLines
    (changelogPath: string)
    (version: Version)
    (today: DateTime)
    (bodyLines: string list)
    : unit =
    let hasUsableUnreleased =
        match validateUnreleased changelogPath with
        | Ok() -> true
        | Error _ -> false

    if hasUsableUnreleased then
        promoteUnreleased changelogPath version today
    else
        let versionHeader =
            sprintf "## %s - %s" (format version) (today.ToString("yyyy-MM-dd"))

        let existing =
            if File.Exists changelogPath then
                File.ReadAllLines changelogPath
            else
                [| "# Changelog"; "" |]

        // Drop an existing empty `## Unreleased` heading (and a single blank
        // line after it) so we don't leave a stray empty section behind the
        // fresh one we insert.
        let withoutEmptyUnreleased =
            match existing |> Array.tryFindIndex isUnreleasedHeading with
            | Some idx ->
                let hasTrailingBlank =
                    idx + 1 < existing.Length && String.IsNullOrWhiteSpace existing[idx + 1]

                let dropCount = if hasTrailingBlank then 2 else 1
                Array.append (existing |> Array.take idx) (existing |> Array.skip (idx + dropCount))
            | None -> existing

        // Insert the new section directly after the top-level `# ` title if
        // present, otherwise at the very top.
        let titleIdx =
            withoutEmptyUnreleased
            |> Array.tryFindIndex (fun l -> l.TrimStart().StartsWith("# ") && not (l.TrimStart().StartsWith("## ")))

        let insertAt =
            match titleIdx with
            | Some i -> i + 1
            | None -> 0

        let freshBlock =
            Array.concat
                [ [| ""; "## Unreleased"; ""; versionHeader; "" |]
                  List.toArray bodyLines
                  [| "" |] ]

        let rebuilt =
            Array.concat
                [ withoutEmptyUnreleased |> Array.take insertAt
                  freshBlock
                  withoutEmptyUnreleased |> Array.skip insertAt ]

        File.WriteAllLines(changelogPath, rebuilt)

let promoteOrInsert (changelogPath: string) (version: Version) (today: DateTime) (defaultBullet: string) : unit =
    promoteOrInsertLines changelogPath version today [ defaultBullet ]

/// A change to a consumer-visible `<PackageReference>` of a packed project
/// between the last release tag and now. Consumer-visible means it lands in the
/// package's nuspec as a dependency, so a consumer's restore observes it.
type PackageRefChange =
    | Added of id: string * version: string
    | Removed of id: string * version: string
    | Bumped of id: string * from: string * ``to``: string

/// The consumer-visible `<PackageReference>` items of an fsproj, as id -> version.
/// Only `Include` items with a version (attribute or child element) count;
/// `PrivateAssets="all"` references are build-only and never reach the nuspec,
/// `Update` items modify an implicit reference, and a versionless item (Central
/// Package Management) has no version here to compare. An id listed more than
/// once (conditional per framework) maps to its distinct versions joined by
/// ", ". `None` when the text is not well-formed XML, so an unreadable project
/// derives nothing rather than everything.
let packageReferences (fsprojXml: string) : Map<string, string> option =
    let child (e: Xml.Linq.XElement) (name: string) =
        match e.Attribute(Xml.Linq.XName.Get name) with
        | null ->
            e.Elements()
            |> Seq.tryFind (fun c -> c.Name.LocalName = name)
            |> Option.map (fun c -> c.Value.Trim())
        | a -> Some(a.Value.Trim())

    try
        let doc = Xml.Linq.XDocument.Parse fsprojXml

        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "PackageReference")
        |> Seq.choose (fun e ->
            let buildOnly =
                child e "PrivateAssets"
                |> Option.exists (fun v -> String.Equals(v, "all", StringComparison.OrdinalIgnoreCase))

            match child e "Include", child e "Version" with
            | Some id, Some version when not buildOnly -> Some(id, version)
            | _ -> None)
        |> Seq.groupBy fst
        |> Seq.map (fun (id, items) -> id, items |> Seq.map snd |> Seq.distinct |> String.concat ", ")
        |> Map.ofSeq
        |> Some
    with _ ->
        None

/// The consumer-visible dependency changes from `before` to `after`, by id.
let diffPackageReferences (before: Map<string, string>) (after: Map<string, string>) : PackageRefChange list =
    let ids = Set.union (before.Keys |> Set.ofSeq) (after.Keys |> Set.ofSeq)

    ids
    |> Set.toList
    |> List.choose (fun id ->
        match before.TryFind id, after.TryFind id with
        | None, Some v -> Some(Added(id, v))
        | Some v, None -> Some(Removed(id, v))
        | Some a, Some b when a <> b -> Some(Bumped(id, a, b))
        | _ -> None)

/// The changelog bullet recording a dependency change.
let dependencyBullet (change: PackageRefChange) : string =
    match change with
    | Added(id, v) -> sprintf "- build(deps): add %s %s" id v
    | Removed(id, v) -> sprintf "- build(deps): remove %s (was %s)" id v
    | Bumped(id, a, b) -> sprintf "- build(deps): bump %s from %s to %s" id a b

/// `token` occurs in `text` as a whole package id or version, not as part of a
/// longer one (`4.1.0-beta.3` must not match `4.1.0-beta.30`). Case-insensitive,
/// as NuGet ids are.
let private mentionsToken (text: string) (token: string) : bool =
    let pattern =
        sprintf @"(?<![\w.-])%s(?![\w-]|\.[\w-])" (Text.RegularExpressions.Regex.Escape token)

    Text.RegularExpressions.Regex.IsMatch(text, pattern, Text.RegularExpressions.RegexOptions.IgnoreCase)

/// A dependency change is already recorded when the text names the package and,
/// unless it was removed, the version it moved to.
let private mentionsChange (text: string) (change: PackageRefChange) : bool =
    match change with
    | Removed(id, _) -> mentionsToken text id
    | Added(id, v)
    | Bumped(id, _, v) -> mentionsToken text id && mentionsToken text v

/// The `## Unreleased` section's non-blank lines as (file line index, text), for
/// a file already known to have content there (`validateUnreleased` is `Ok`).
let private unreleasedEntries (lines: string[]) : (int * string)[] =
    lines
    |> Array.indexed
    |> Array.skip (1 + Array.findIndex isUnreleasedHeading lines)
    |> Array.takeWhile (fun (_, l) -> not (isLevel2Heading l))
    |> Array.filter (fun (_, l) -> not (String.IsNullOrWhiteSpace l))

/// The entry lines promotion will publish as `changelogPath`'s release notes,
/// before dependency bullets: the authored `## Unreleased` entries when the section
/// has content, otherwise the bullets derived from `descriptions` — the same choice
/// `planPromotion` makes. `descriptions` is only called when the section is
/// unauthored, so an authored changelog costs no VCS query. Read for the bump a
/// release declares (`DeclaredBump`); dependency bullets are tool-written, not
/// declared, so they are not included.
let promotedEntryLines (changelogPath: string) (descriptions: unit -> string list) : string list =
    match validateUnreleased changelogPath with
    | Ok() -> unreleasedEntries (File.ReadAllLines changelogPath) |> Seq.map snd |> Seq.toList
    | Error _ -> deriveUnreleasedBullets (descriptions ())

/// Where the promoted section's entries come from.
type UnreleasedSource =
    /// The hand-authored `## Unreleased` section, promoted as written. Commit
    /// summaries are NOT merged into it: an author who wrote the section owns it.
    | Authored
    /// The section is missing or empty, so it is filled from commit summaries
    /// (`deriveUnreleasedBullets`, possibly none when only dependencies changed).
    | Derived of bullets: string list

/// What promoting a changelog will write. Computed once by `planPromotion` and
/// used by BOTH `release --check` and the release itself, so what the check
/// reports is, by construction, what promotion delivers.
type PromotionPlan =
    {
        Source: UnreleasedSource
        /// Consumer-visible dependency changes the section does not already name,
        /// appended after its entries. Derived from the fsproj, never from prose,
        /// so a dependency bump cannot vanish behind an authored section.
        DependencyBullets: string list
    }

/// Plan the promotion of `changelogPath`'s `## Unreleased` section.
///
/// An authored section is promoted as written; `descriptions` are only used
/// when it is missing or empty. `dependencyChanges` are always recorded unless
/// the section (authored text, or the derived bullets) already names them.
/// `Error` — and nothing to write — when the section is unauthored and neither
/// commits nor dependency changes give anything to derive.
let planPromotion
    (changelogPath: string)
    (descriptions: string list)
    (dependencyChanges: PackageRefChange list)
    : Result<PromotionPlan, ChangelogError> =
    let unmentioned (text: string) =
        dependencyChanges
        |> List.filter (fun c -> not (mentionsChange text c))
        |> List.map dependencyBullet

    match validateUnreleased changelogPath with
    | Ok() ->
        let body =
            String.Join("\n", unreleasedEntries (File.ReadAllLines changelogPath) |> Seq.map snd)

        Ok
            { Source = Authored
              DependencyBullets = unmentioned body }
    | Error err ->
        let bullets = deriveUnreleasedBullets descriptions
        let dependencyBullets = unmentioned (String.concat "\n" bullets)

        if List.isEmpty bullets && List.isEmpty dependencyBullets then
            Error err
        else
            Ok
                { Source = Derived bullets
                  DependencyBullets = dependencyBullets }

/// Write `plan` (from `planPromotion` on the same file) as the `version` section.
/// An authored section keeps its text and order; dependency bullets follow its
/// last entry, so a leading callout stays first.
let applyPromotion (changelogPath: string) (version: Version) (today: DateTime) (plan: PromotionPlan) : unit =
    match plan.Source with
    | Derived bullets -> promoteOrInsertLines changelogPath version today (bullets @ plan.DependencyBullets)
    | Authored ->
        if not (List.isEmpty plan.DependencyBullets) then
            let lines = File.ReadAllLines changelogPath

            let lastEntry = unreleasedEntries lines |> Seq.map fst |> Seq.last

            let withDependencies =
                Array.concat
                    [ lines |> Array.take (lastEntry + 1)
                      List.toArray plan.DependencyBullets
                      lines |> Array.skip (lastEntry + 1) ]

            File.WriteAllLines(changelogPath, withDependencies)

        promoteUnreleased changelogPath version today

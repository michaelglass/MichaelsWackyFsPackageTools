module FsSemanticTagger.Changelog

open System
open System.IO
open FsSemanticTagger.Version

type ChangelogError =
    | NoFile of path: string
    | NoUnreleasedSection of path: string
    | EmptyUnreleasedSection of path: string

let formatError (err: ChangelogError) : string =
    match err with
    | NoFile p -> sprintf "%s: CHANGELOG.md not found" p
    | NoUnreleasedSection p -> sprintf "%s: no '## Unreleased' section" p
    | EmptyUnreleasedSection p -> sprintf "%s: '## Unreleased' section is empty" p

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

let validateUnreleased (changelogPath: string) : Result<unit, ChangelogError> =
    if not (File.Exists changelogPath) then
        Error(NoFile changelogPath)
    else
        let lines = File.ReadAllLines changelogPath
        let unreleasedIdx = lines |> Array.tryFindIndex isUnreleasedHeading

        match unreleasedIdx with
        | None -> Error(NoUnreleasedSection changelogPath)
        | Some idx ->
            let hasEntry =
                seq {
                    for i in (idx + 1) .. (lines.Length - 1) do
                        yield lines[i]
                }
                |> Seq.takeWhile (fun l -> not (isLevel2Heading l))
                |> Seq.exists (fun l -> not (String.IsNullOrWhiteSpace l))

            if hasEntry then
                Ok()
            else
                Error(EmptyUnreleasedSection changelogPath)

/// Conventional-commit types recognised for changelog grouping. An unrecognised
/// prefix (or no prefix) falls into the "other" group and is kept verbatim.
let private conventionalTypes =
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
let private conventionalPrefixRegex =
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
/// descriptions (AUTOMATION-197). Only each description's SUMMARY line (its
/// first non-blank line) is used — jj descriptions are long and multi-line, and
/// the changelog stays readable with one bullet per commit. Each summary is
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

/// Promote `## Unreleased`, DERIVING the section content from `descriptions`
/// (AUTOMATION-197) when — and only when — the section is missing or empty. A
/// hand-authored `## Unreleased` is NEVER clobbered: if `validateUnreleased` is
/// `Ok`, this behaves exactly like `promoteUnreleased` and `descriptions` is
/// ignored. Otherwise the commit descriptions are turned into grouped bullets
/// (`deriveUnreleasedBullets`) and promoted into a fresh version section. When
/// there is nothing to promote and nothing derivable (no qualifying commits),
/// returns `Error(EmptyUnreleasedSection ...)` and writes nothing — the caller
/// surfaces the enforce error rather than promoting an empty section.
let promoteOrDerive
    (changelogPath: string)
    (version: Version)
    (today: DateTime)
    (descriptions: string list)
    : Result<unit, ChangelogError> =
    match validateUnreleased changelogPath with
    | Ok() ->
        promoteUnreleased changelogPath version today
        Ok()
    | Error _ ->
        match deriveUnreleasedBullets descriptions with
        | [] -> Error(EmptyUnreleasedSection changelogPath)
        | bullets ->
            promoteOrInsertLines changelogPath version today bullets
            Ok()

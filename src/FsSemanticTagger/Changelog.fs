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
let promoteOrInsert (changelogPath: string) (version: Version) (today: DateTime) (defaultBullet: string) : unit =
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

        let freshBlock = [| ""; "## Unreleased"; ""; versionHeader; ""; defaultBullet; "" |]

        let rebuilt =
            Array.concat
                [ withoutEmptyUnreleased |> Array.take insertAt
                  freshBlock
                  withoutEmptyUnreleased |> Array.skip insertAt ]

        File.WriteAllLines(changelogPath, rebuilt)

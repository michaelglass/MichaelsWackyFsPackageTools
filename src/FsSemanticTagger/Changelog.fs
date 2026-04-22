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

let internal isLevel2Heading (line: string) : bool = line.TrimStart().StartsWith("## ")

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

    let before = if idx = 0 then [||] else lines[.. idx - 1]
    let after = lines[idx + 1 ..]

    let freshBlock = [| "## Unreleased"; ""; versionHeader |]
    let rebuilt = Array.concat [ before; freshBlock; after ]

    File.WriteAllLines(changelogPath, rebuilt)

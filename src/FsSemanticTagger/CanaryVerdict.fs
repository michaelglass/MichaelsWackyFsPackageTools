/// The consumer canary's recorded verdict, and when a resumed release may reuse it.
///
/// A release that pushed its bump commit and then stopped (a CI wait that gave up,
/// a lost connection) resumes from that commit. Running the canary again there
/// tests nothing new: the bump commit changes only versions and changelogs. Worse,
/// it can never pass once the plan bumps a dependency the consumed package bundles,
/// because the post-bump tree names that dependency at a version this same release
/// has not published yet. Observed 2026-11-05 in the FsHotWatch release (NU1102).
///
/// So every canary verdict is recorded, keyed by the release plan and the consumers
/// it ran, with the commit it was run on. A resumed run reuses a recorded PASS when
/// the only change from that commit to its own is the release's version bump. A
/// refusal is never reused, and neither is a pass on any other content.
module FsSemanticTagger.CanaryVerdict

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type Outcome =
    | CanaryPassed
    | CanaryRefused

/// The record as it is stored: public, because System.Text.Json reads only public
/// properties, and an F# private type has no public ones.
[<CLIMutable>]
type Stored =
    {
        Plan: string array
        Consumers: string array
        Content: string
        Outcome: string
        RecordedAt: DateTimeOffset
    }

type Record =
    {
        /// The release plan, one `name version` per package, sorted.
        Plan: string list
        /// The consumers the plan selected, one line per consumer, sorted.
        Consumers: string list
        /// The commit whose tree the candidates were packed from.
        Content: string
        Outcome: Outcome
        RecordedAt: DateTimeOffset
    }

/// The record's file: one per (plan, consumers), so the newest verdict for a plan
/// replaces the older one.
let fileFor (dir: string) (plan: string list) (consumers: string list) : string =
    let key = String.concat "\n" (plan @ [ "" ] @ consumers)
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes key) |> Convert.ToHexString
    Path.Combine(dir, hash.Substring(0, 16).ToLowerInvariant() + ".json")

let private storedJson =
    JsonSerializerOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let toJson (record: Record) : string =
    JsonSerializer.Serialize(
        {
            Stored.Plan = List.toArray record.Plan
            Consumers = List.toArray record.Consumers
            Content = record.Content
            Outcome =
                match record.Outcome with
                | CanaryPassed -> "passed"
                | CanaryRefused -> "refused"
            RecordedAt = record.RecordedAt
        },
        storedJson
    )

/// A record as `toJson` writes it; anything else, including a file cut short, is
/// no record, and the canary runs.
let parse (json: string) : Record option =
    try
        let stored = JsonSerializer.Deserialize<Stored>(json, storedJson)

        let outcome =
            match stored.Outcome with
            | "passed" -> Some CanaryPassed
            | "refused" -> Some CanaryRefused
            | _ -> None

        outcome
        |> Option.map (fun outcome ->
            {
                Plan = List.ofArray stored.Plan
                Consumers = List.ofArray stored.Consumers
                Content = stored.Content
                Outcome = outcome
                RecordedAt = stored.RecordedAt
            })
    with _ ->
        None

let save (dir: string) (record: Record) : unit =
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(fileFor dir record.Plan record.Consumers, toJson record)

/// The newest verdict recorded for exactly this plan and these consumers.
let load (dir: string) (plan: string list) (consumers: string list) : Record option =
    let path = fileFor dir plan consumers

    if File.Exists path then
        parse (File.ReadAllText path)
        |> Option.filter (fun record -> record.Plan = plan && record.Consumers = consumers)
    else
        None

type Reuse =
    | Reusable of Record
    | NotReusable of reason: string

/// May a run at commit `head` reuse `record`? Only a pass, and only when `head` is
/// the commit it was taken on or `bumpOnlySince` says every change from that commit
/// to `head` is the release's own version bump.
let decide (record: Record option) (head: string) (bumpOnlySince: string -> Result<unit, string>) : Reuse =
    match record with
    | None -> NotReusable "no canary verdict is recorded for this release plan"
    | Some recorded when recorded.Outcome = CanaryRefused ->
        NotReusable(sprintf "the canary recorded for this release plan on %s refused" recorded.Content)
    | Some recorded when recorded.Content = head -> Reusable recorded
    | Some recorded ->
        match bumpOnlySince recorded.Content with
        | Ok() -> Reusable recorded
        | Error reason -> NotReusable reason

let private versionElement =
    Regex("<Version>[^<]*</Version>", RegexOptions.Compiled)

/// Do two fsproj texts differ in nothing but their `<Version>`?
let onlyVersionDiffers (before: string) (after: string) : bool =
    versionElement.Replace(before, "<Version/>") = versionElement.Replace(after, "<Version/>")

/// Is every path in `changed` (from `baseRev` to `head`) the release's own bump: a
/// changelog it promotes, or one of its fsprojs changed in nothing but `<Version>`?
/// `fileAt rev path` reads a file at a revision. Paths are repo-relative; `fsprojs`
/// and `changelogs` use forward slashes.
let bumpOnly
    (fsprojs: Set<string>)
    (changelogs: Set<string>)
    (changed: string list)
    (fileAt: string -> string -> string option)
    (baseRev: string)
    (head: string)
    : Result<unit, string> =
    let check (path: string) : Result<unit, string> =
        if changelogs.Contains path then
            Ok()
        elif fsprojs.Contains path then
            match fileAt baseRev path, fileAt head path with
            | Some before, Some after when onlyVersionDiffers before after -> Ok()
            | _ -> Error(sprintf "%s changed since the canary ran beyond its <Version>" path)
        else
            Error(
                sprintf "%s changed since the canary ran, and it is not a version or changelog the release bumps" path
            )

    changed
    |> List.map (fun path -> path.Trim().Replace('\\', '/'))
    |> List.filter (fun path -> path <> "")
    |> List.fold (fun acc path -> acc |> Result.bind (fun () -> check path)) (Ok())

#!/usr/bin/env dotnet fsi

/// Repo policy gate (AUTOMATION-213): a release must not be cuttable unless the
/// LOCAL gate is green.
///
/// Why this exists. `mise run ci` is the only gate that sees the macOS coverage
/// floors — GitHub CI runs ubuntu-latest only, and CoverageRatchet selects the
/// override matching the current platform, so a red macOS floor is structurally
/// invisible to remote CI. When `release` depended on `build` instead of `ci`,
/// a release could be cut from a tree whose local gate was red and nothing
/// anywhere would say so. That is the half of AUTOMATION-213 that mattered: a
/// floor that can be crossed without anything failing is not a floor.
///
/// What it asserts, against `mise` itself rather than a hand-parsed mise.toml:
///
///   1. Every PUBLISHING task (one that invokes the FsSemanticTagger release
///      CLI in a mutating mode) has `ci` in its transitive `depends`. mise fails
///      a task closed when a dependency exits non-zero — the dependent's `run`
///      never executes — so this wiring is what makes a red gate unpublishable.
///   2. At least one publishing task was found. Without this the check would
///      pass vacuously the day the detection below stops matching.
///   3. `ci` itself still reaches `coverage-check`. The dependency in (1) is
///      only worth having while the gate it points at contains the check the
///      release path is being gated on.
///
/// The gate is a `depends` edge, not a recorded green marker, so it cannot go
/// stale: it runs in the same invocation, on the tree being released.
///
/// Usage: dotnet fsi scripts/check-release-gate.fsx   (exit 1 on any failure)

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

/// The gate every publishing task must depend on, and the check that gate must
/// still contain.
let gateTask = "ci"
let requiredGateStep = "coverage-check"

let repoRoot = Directory.GetCurrentDirectory()

let runMise (args: string list) : string =
    let psi = ProcessStartInfo("mise")
    psi.WorkingDirectory <- repoRoot
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    for a in args do
        psi.ArgumentList.Add(a)

    use p = Process.Start(psi)
    // Drain both pipes concurrently: mise writes update notices to stderr, and a
    // blocking read of one stream while the other fills its buffer deadlocks.
    let stdoutTask = Task.Run(fun () -> p.StandardOutput.ReadToEnd())
    let stderrTask = Task.Run(fun () -> p.StandardError.ReadToEnd())
    p.WaitForExit()

    if p.ExitCode <> 0 then
        eprintfn "mise %s failed (exit %d):\n%s" (String.Join(" ", args)) p.ExitCode stderrTask.Result
        exit 1

    stdoutTask.Result

type MiseTask =
    { Name: string
      Depends: string list
      Run: string list }

let strings (el: JsonElement) (prop: string) : string list =
    match el.TryGetProperty(prop) with
    | true, arr when arr.ValueKind = JsonValueKind.Array ->
        arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
    | _ -> []

/// Every task mise would run in THIS repo. Tasks contributed by a user-level or
/// parent-directory config are dropped: repo policy answers for repo tasks.
let tasks =
    let doc = JsonDocument.Parse(runMise [ "tasks"; "ls"; "--json" ])

    doc.RootElement.EnumerateArray()
    |> Seq.filter (fun el ->
        match el.TryGetProperty("source") with
        | true, src -> (src.GetString() |> Path.GetFullPath).StartsWith(repoRoot)
        | _ -> false)
    |> Seq.map (fun el ->
        { Name = el.GetProperty("name").GetString()
          Depends = strings el "depends"
          Run = strings el "run" })
    |> List.ofSeq

let byName = tasks |> List.map (fun t -> t.Name, t) |> Map.ofList

/// A task PUBLISHES if it drives the release CLI in a mode that mutates the
/// world (bumps, commits, tags, pushes). `release --check` and `--dry-run` are
/// read-only pre-flights and are not publishing.
let readOnlyFlags = [ "--check"; "--dry-run" ]
let releaseVerb = Regex(@"--\s+(release|alpha)\b")

let isPublishing (t: MiseTask) =
    t.Run
    |> List.exists (fun line ->
        releaseVerb.IsMatch(line)
        && not (readOnlyFlags |> List.exists line.Contains))

/// Transitive `depends` closure. mise runs the whole closure to completion
/// before the task's own `run`, and aborts the task if any of it fails.
let rec closure (seen: Set<string>) (name: string) : Set<string> =
    match Map.tryFind name byName with
    | Some t when not (Set.contains name seen) ->
        t.Depends |> List.fold (fun acc d -> closure acc d) (Set.add name seen)
    | _ -> Set.add name seen

let mutable failed = false

let fail (msg: string) =
    printfn "  FAIL %s" msg
    failed <- true

let pass (msg: string) = printfn "  PASS %s" msg

// 1 + 2 — publishing tasks exist, and each one runs the local gate first.
let publishing = tasks |> List.filter isPublishing

if List.isEmpty publishing then
    fail (
        sprintf
            "no publishing task found in mise.toml. Either the release tasks were removed, \
             or they no longer match this check's detection (a run line invoking the release \
             CLI as `-- release` / `-- alpha` without %s) — in which case fix the detection, \
             because an undetected publishing task is an ungated one."
            (String.Join("/", readOnlyFlags))
    )
else
    for t in publishing |> List.sortBy (fun t -> t.Name) do
        let deps = closure Set.empty t.Name

        if Set.contains gateTask deps then
            pass (sprintf "publishing task '%s' depends on '%s'" t.Name gateTask)
        else
            fail (
                sprintf
                    "publishing task '%s' does not depend on '%s' (depends: %s). A release \
                     cut this way is never checked against the local gate — which is the only \
                     gate that sees the macOS coverage floors, since GitHub CI is linux-only."
                    t.Name
                    gateTask
                    (String.Join(", ", t.Depends))
            )

// 3 — the gate still contains the check the release path is being gated on.
if not (Map.containsKey gateTask byName) then
    fail (sprintf "no '%s' task in mise.toml — there is no local gate to depend on" gateTask)
elif closure Set.empty gateTask |> Set.contains requiredGateStep then
    pass (sprintf "'%s' reaches '%s'" gateTask requiredGateStep)
else
    fail (
        sprintf
            "'%s' no longer reaches '%s', so gating the release path on it would not gate \
             coverage at all"
            gateTask
            requiredGateStep
    )

if failed then
    printfn "\nRelease gate check FAILED"
    exit 1
else
    printfn "\nRelease path is gated on the local '%s' run" gateTask

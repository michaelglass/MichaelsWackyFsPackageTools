/// How long `release` waits for CI on the release commit before refusing.
///
/// A fixed budget cannot fit every repo: one repo's CI finishes in two minutes,
/// another's takes seventeen. So the budget is read from the repo's own record —
/// twice the median duration of its recent successful runs of the required
/// workflow, never less than `floorTimeout` — unless `semantic-tagger.json` sets
/// `ciTimeoutMinutes`. Whatever the budget, a timeout still refuses the release.
module FsSemanticTagger.CiWait

open System

/// Where a budget came from, so the operator is told what to expect and why.
type Basis =
    /// `ciTimeoutMinutes` in `semantic-tagger.json`.
    | Configured
    /// Sized from the median of this many successful runs.
    | FromHistory of median: TimeSpan * runs: int
    /// The history was read and holds no successful run.
    | NoHistory
    /// The history could not be read.
    | HistoryUnavailable of reason: string

type Budget = { Timeout: TimeSpan; Basis: Basis }

/// How many recent successful runs the estimate is taken over.
let historyRuns = 10

/// The shortest wait sized from history, so a fast CI still tolerates a slow
/// runner or a queue, and the wait when there is no successful run to go on.
let floorTimeout = TimeSpan.FromMinutes 5.0

/// The wait when the history cannot be read. Nothing is known about this repo's
/// CI, so it keeps the fixed budget the tool used before waits were sized.
let unavailableHistoryTimeout = TimeSpan.FromMinutes 15.0

/// The middle duration; for an even count, the mean of the two middle ones.
let median (durations: TimeSpan list) : TimeSpan option =
    match List.sort durations with
    | [] -> None
    | sorted ->
        let n = sorted.Length

        if n % 2 = 1 then
            Some sorted[n / 2]
        else
            Some(TimeSpan.FromTicks((sorted[n / 2 - 1].Ticks + sorted[n / 2].Ticks) / 2L))

/// The budget for one wait. A configured timeout is used as given and the
/// history is not read at all.
let size (configured: TimeSpan option) (history: unit -> Result<TimeSpan list, string>) : Budget =
    match configured with
    | Some timeout ->
        {
            Timeout = timeout
            Basis = Configured
        }
    | None ->
        match history () with
        | Error reason ->
            {
                Timeout = unavailableHistoryTimeout
                Basis = HistoryUnavailable reason
            }
        | Ok durations ->
            match median durations with
            | None ->
                {
                    Timeout = floorTimeout
                    Basis = NoHistory
                }
            | Some middle ->
                {
                    Timeout = max floorTimeout (TimeSpan.FromTicks(middle.Ticks * 2L))
                    Basis = FromHistory(middle, durations.Length)
                }

/// How many CI checks `intervalMs` apart cover the budget. The poll sleeps
/// between checks, not after the last one, so it is one more than the sleeps.
let attempts (intervalMs: int) (budget: Budget) : int =
    let interval = float (max 1 intervalMs)
    1 + int (Math.Ceiling(budget.Timeout.TotalMilliseconds / interval))

/// A duration as an operator reads it: `4m12s`, or `0.3s` under a minute.
let formatDuration (elapsed: TimeSpan) : string =
    if elapsed.TotalMinutes >= 1.0 then
        sprintf "%dm%02ds" (int elapsed.TotalMinutes) elapsed.Seconds
    else
        sprintf "%.1fs" elapsed.TotalSeconds

/// What the operator is told when the wait starts.
let describe (budget: Budget) : string =
    let giveUp = formatDuration budget.Timeout

    match budget.Basis with
    | FromHistory(middle, runs) ->
        sprintf
            "expected ~%s, the median of the last %d successful CI runs; giving up after %s"
            (formatDuration middle)
            runs
            giveUp
    | NoHistory -> sprintf "no successful CI run to estimate from; giving up after %s" giveUp
    | HistoryUnavailable reason -> sprintf "CI history unavailable (%s); giving up after %s" reason giveUp
    | Configured -> sprintf "giving up after %s, the configured ciTimeoutMinutes" giveUp

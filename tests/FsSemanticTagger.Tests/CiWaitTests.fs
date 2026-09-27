module FsSemanticTagger.Tests.CiWaitTests

open System
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.CiWait

/// A configured budget that polls exactly `attempts` times at `intervalMs`, for
/// release tests that script a sequence of CI answers and must not consult history.
let fixedCiWait (intervalMs: int) (attempts: int) : unit -> Budget =
    fun () ->
        { Timeout = TimeSpan.FromMilliseconds(float ((attempts - 1) * max 1 intervalMs))
          Basis = Configured }

let private minutes (m: float) = TimeSpan.FromMinutes m
let private seconds (s: int) = TimeSpan.FromSeconds(float s)

let private noHistoryExpected () : Result<TimeSpan list, string> =
    failwith "history must not be read when a timeout is configured"

// median

[<Fact>]
let ``median - odd count is the middle value, even count the mean of the two middle values`` () =
    test <@ median [ minutes 3.0; minutes 1.0; minutes 2.0 ] = Some(minutes 2.0) @>
    test <@ median [ minutes 4.0; minutes 1.0; minutes 2.0; minutes 3.0 ] = Some(minutes 2.5) @>
    test <@ median [] = None @>

// size

[<Fact>]
let ``size - twice the median of the history, which the operator is shown as the expectation`` () =
    // FsHotWatch's CI, eight successful runs measured at 15.5-17.7 minutes.
    let history = [ 930; 960; 972; 990; 1008; 1020; 1038; 1062 ] |> List.map seconds

    let budget = size None (fun () -> Ok history)

    test <@ budget.Basis = FromHistory(seconds 999, 8) @>
    test <@ budget.Timeout = seconds 1998 @>

[<Fact>]
let ``size - a fast CI is never given less than the floorTimeout`` () =
    let budget = size None (fun () -> Ok [ minutes 1.0; minutes 1.5; minutes 2.0 ])

    test <@ budget.Timeout = floorTimeout @>
    test <@ floorTimeout = minutes 5.0 @>
    test <@ budget.Basis = FromHistory(minutes 1.5, 3) @>

[<Fact>]
let ``size - no successful run to learn from waits the floorTimeout`` () =
    let budget = size None (fun () -> Ok [])

    test
        <@
            budget = { Timeout = floorTimeout
                       Basis = NoHistory }
        @>

[<Fact>]
let ``size - history that cannot be read waits the fixed fallback, not the floorTimeout`` () =
    let budget = size None (fun () -> Error "gh: not authenticated")

    test <@ budget.Timeout = unavailableHistoryTimeout @>
    test <@ unavailableHistoryTimeout = minutes 15.0 @>
    test <@ budget.Basis = HistoryUnavailable "gh: not authenticated" @>

[<Fact>]
let ``size - a configured timeout is honoured as given and history is never read`` () =
    let budget = size (Some(minutes 45.0)) noHistoryExpected

    test
        <@
            budget = { Timeout = minutes 45.0
                       Basis = Configured }
        @>

[<Fact>]
let ``size - a configured timeout below the floorTimeout is still honoured`` () =
    let budget = size (Some(minutes 2.0)) noHistoryExpected

    test <@ budget.Timeout = minutes 2.0 @>

// attempts

[<Fact>]
let ``attempts - enough checks that the sleeps between them cover the timeout`` () =
    let budget =
        { Timeout = seconds 1998
          Basis = Configured }

    // 15s apart: ceil(1998 / 15) = 134 sleeps, so 135 checks.
    test <@ attempts 15000 budget = 135 @>
    test <@ attempts 15000 { budget with Timeout = floorTimeout } = 21 @>
    test <@ attempts 15000 { budget with Timeout = TimeSpan.Zero } = 1 @>

[<Fact>]
let ``fixedCiWait - a test budget polls exactly the attempts it names`` () =
    test <@ attempts 0 (fixedCiWait 0 10 ()) = 10 @>
    test <@ attempts 0 (fixedCiWait 0 1 ()) = 1 @>
    test <@ attempts 25 (fixedCiWait 25 3 ()) = 3 @>

// describe

[<Fact>]
let ``describe - history names the expectation and where it came from`` () =
    let text =
        describe
            { Timeout = seconds 1998
              Basis = FromHistory(seconds 999, 8) }

    test <@ text = "expected ~16m39s, the median of the last 8 successful CI runs; giving up after 33m18s" @>

[<Fact>]
let ``describe - each fallback says why there is no expectation`` () =
    test
        <@
            describe
                { Timeout = floorTimeout
                  Basis = NoHistory } = "no successful CI run to estimate from; giving up after 5m00s"
        @>

    test
        <@
            describe
                { Timeout = unavailableHistoryTimeout
                  Basis = HistoryUnavailable "boom" } = "CI history unavailable (boom); giving up after 15m00s"
        @>

    test
        <@
            describe
                { Timeout = minutes 45.0
                  Basis = Configured } = "giving up after 45m00s, the configured ciTimeoutMinutes"
        @>

// the history query

let private historyArgs =
    "run list --workflow .github/workflows/ci.yml --status success --limit 10 --json startedAt,updatedAt"

let private ghAnswering (result: CommandResult) : string -> string -> CommandResult =
    fun cmd args ->
        if cmd = "gh" && args = historyArgs then
            result
        else
            Failure(sprintf "unexpected: %s %s" cmd args, 1)

[<Fact>]
let ``successfulRunDurations - each run lasts from its start to its last update`` () =
    let json =
        """[{"startedAt":"2026-09-27T10:00:00Z","updatedAt":"2026-09-27T10:16:30Z"},
            {"startedAt":"2026-09-27T09:00:00Z","updatedAt":"2026-09-27T09:15:30Z"}]"""

    let result =
        FsSemanticTagger.Vcs.successfulRunDurations (ghAnswering (Success json)) 10

    test <@ result = Ok [ minutes 16.5; minutes 15.5 ] @>

[<Fact>]
let ``successfulRunDurations - a run with no measurable duration is left out`` () =
    let json =
        """[{"startedAt":"2026-09-27T10:00:00Z","updatedAt":"2026-09-27T10:00:00Z"},
            {"startedAt":"2026-09-27T09:00:00Z","updatedAt":"2026-09-27T09:10:00Z"}]"""

    let result =
        FsSemanticTagger.Vcs.successfulRunDurations (ghAnswering (Success json)) 10

    test <@ result = Ok [ minutes 10.0 ] @>

[<Fact>]
let ``successfulRunDurations - a failed gh call or an unreadable answer is an Error, not an empty history`` () =
    test <@ Result.isError (FsSemanticTagger.Vcs.successfulRunDurations (ghAnswering (Failure("no gh", 1))) 10) @>
    test <@ Result.isError (FsSemanticTagger.Vcs.successfulRunDurations (ghAnswering (Success "not json")) 10) @>
    test <@ Result.isError (FsSemanticTagger.Vcs.successfulRunDurations (ghAnswering (Success """[{"x":1}]""")) 10) @>

module SyncDocs.Tests.TestHelpersTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Tests.Common
open Swensen.Unquote
open Tests.Common.TestHelpers

let private linesOf (s: string) =
    s.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

let private expectedLines id =
    [
        for i in 1..5 do
            $"worker {id} direct {i}"
            $"worker {id} task {i}"
            $"worker {id} async {i}"
            $"worker {id} thread {i}"
    ]

[<Fact>]
let ``withCapturedConsole - concurrent captures only see their own output`` () =
    let workers = 16
    // Every worker holds its capture open before any of them prints, so a
    // capture shared through the global Console.Out cannot go unnoticed.
    use barrier = new Barrier(workers)
    let results: Result<string, exn>[] = Array.create workers (Ok "")

    let work id () =
        try
            let printed, () =
                withCapturedConsole (fun () ->
                    barrier.SignalAndWait(TimeSpan.FromSeconds 3.0) |> ignore

                    for i in 1..5 do
                        printfn $"worker {id} direct {i}"
                        Task.Run(fun () -> printfn $"worker {id} task {i}").Wait()

                        async {
                            do! Async.SwitchToThreadPool()
                            printfn $"worker {id} async {i}"
                        }
                        |> Async.RunSynchronously

                        let t = Thread(fun () -> printfn $"worker {id} thread {i}")
                        t.Start()
                        t.Join())

            results[id] <- Ok printed
        with ex ->
            results[id] <- Error ex

    let threads = [ for id in 0 .. workers - 1 -> Thread(work id) ]
    threads |> List.iter _.Start()
    threads |> List.iter _.Join()

    for id in 0 .. workers - 1 do
        let actual = results[id] |> Result.map linesOf
        test <@ actual = Ok(expectedLines id) @>

[<Fact>]
let ``withCapturedConsole - nested capture restores the outer one`` () =
    let outer, inner =
        withCapturedConsole (fun () ->
            printfn "outer before"
            let inner, () = withCapturedConsole (fun () -> printfn "inner")
            printfn "outer after"
            inner)

    test <@ inner = "inner\n" @>
    test <@ outer = "outer before\nouter after\n" @>

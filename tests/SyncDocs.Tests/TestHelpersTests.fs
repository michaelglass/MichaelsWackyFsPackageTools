module SyncDocs.Tests.TestHelpersTests

open System
open System.Diagnostics
open System.IO
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

/// Many threads write to Console.Out, inside and outside captures, while another thread swaps
/// Console.Out between a plain console writer and a freshly installed router, so writes are
/// always in flight on a writer that has just been replaced. Explicit: it runs only in the child
/// process the next test starts, because a deadlock on the console locks would also hang the
/// test platform's own output.
[<Fact(Explicit = true, Timeout = 30000)>]
let ``console stress - captured and uncaptured writes while the router reinstalls`` () =
    let printers =
        [
            for _ in 1..8 ->
                Thread(fun () ->
                    for _ in 1..5000 do
                        printf " ")
        ]

    // While Console.Out is the plain writer, a capture's output goes to the console instead,
    // so captures are written but not checked here.
    let capturers =
        [
            for id in 1..4 ->
                Thread(fun () ->
                    for _ in 1..500 do
                        withCapturedConsole (fun () -> printf $"{id}") |> ignore)
        ]

    let standardOutput = Console.OpenStandardOutput()

    let installer =
        Thread(fun () ->
            for _ in 1..500 do
                Console.SetOut(new StreamWriter(standardOutput, AutoFlush = true, leaveOpen = true))
                // Keeps the plain writer current long enough for printers to start writes on it.
                Thread.SpinWait 20000
                installConsoleRouter ())

    let threads = installer :: printers @ capturers
    threads |> List.iter _.Start()
    threads |> List.iter _.Join()

[<Fact(Timeout = 120000)>]
let ``withCapturedConsole - concurrent console writes do not deadlock while the router installs`` () =
    let host = Environment.ProcessPath

    let psi =
        ProcessStartInfo(host, RedirectStandardOutput = true, RedirectStandardError = true)

    // Under `dotnet test` the host can be `dotnet` running the test assembly rather than its apphost.
    if Path.GetFileNameWithoutExtension(host) = "dotnet" then
        psi.ArgumentList.Add(Reflection.Assembly.GetEntryAssembly().Location)

    for arg in [ "--explicit"; "only"; "--filter-method"; "*console stress*" ] do
        psi.ArgumentList.Add arg

    use child = Process.Start psi
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    let exited = child.WaitForExit(TimeSpan.FromSeconds 60.0)

    if not exited then
        child.Kill(true)

    // Drops the stress test's runs of spaces, leaving the test platform's report.
    let report =
        (stdout.Result + stderr.Result).Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (fun line -> line <> "")
        |> String.concat "\n"

    if not exited then
        failwith $"console stress run hung:\n{report}"

    if child.ExitCode <> 0 then
        failwith $"console stress run exited {child.ExitCode}:\n{report}"

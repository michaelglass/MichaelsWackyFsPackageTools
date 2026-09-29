module FsSemanticTagger.Shell

open System.Diagnostics
open System.Threading.Tasks

type CommandResult =
    | Success of string
    | Failure of string * exitCode: int

let private startInfo (cmd: string) (args: string) : ProcessStartInfo =
    let psi = ProcessStartInfo(cmd, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi

let private execute (psi: ProcessStartInfo) : CommandResult =
    let p = Process.Start(psi)
    // Read stdout and stderr concurrently to avoid deadlocks
    let stdoutTask = Task.Run(fun () -> p.StandardOutput.ReadToEnd())
    let stderrTask = Task.Run(fun () -> p.StandardError.ReadToEnd())
    let stdout = stdoutTask.Result
    let stderr = stderrTask.Result
    p.WaitForExit()

    if p.ExitCode = 0 then
        Success(stdout.TrimEnd())
    else
        let msg =
            if stderr.Trim() <> "" then
                stderr.TrimEnd()
            else
                stdout.TrimEnd()

        Failure(msg, p.ExitCode)

let run (cmd: string) (args: string) : CommandResult = execute (startInfo cmd args)

let runOrFail (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure(error, _) -> failwithf "%s %s failed: %s" cmd args error

let runSilent (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

/// `run` in the working directory `cwd`.
let runIn (cwd: string) (cmd: string) (args: string) : CommandResult =
    let psi = startInfo cmd args
    psi.WorkingDirectory <- cwd
    execute psi

/// `run`, with the `git` and `gh` processes it starts pointed at the git store
/// `gitDir` (see `Shared.GitStoreEnvironment.environmentFor`). A jj checkout with no colocated
/// `.git` needs this for either to find the repository; `None` runs them as `run`
/// would. The variable is set on each child process, so it never reaches git
/// processes started elsewhere in this one.
let runWithGitDir (gitDir: string option) (cmd: string) (args: string) : CommandResult =
    let psi = startInfo cmd args

    for name, value in Shared.GitStoreEnvironment.environmentFor gitDir cmd do
        psi.Environment[name] <- value

    execute psi

/// How a time-boxed command ended: exited, or killed when the budget ran out.
type GateOutcome =
    | Exited of exitCode: int
    | TimedOut of budget: System.TimeSpan

/// How long `runLogged` keeps reading the gate's output after the gate has
/// ended. The gate's own output is already in the pipes by then and drains in
/// milliseconds; only a process the gate left running holds them open longer.
let drainGrace = System.TimeSpan.FromSeconds 5.0

/// `runLogged` with the drain grace `grace`.
let runLoggedWithin
    (grace: System.TimeSpan)
    (cwd: string)
    (command: string)
    (timeout: System.TimeSpan)
    (logPath: string)
    : GateOutcome =
    let psi = ProcessStartInfo("sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add command
    psi.WorkingDirectory <- cwd
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName logPath)
    |> ignore

    // try/finally and a synchronized writer instead of `use`/`lock`: those add
    // unreachable branches that the coverage floors would count.
    let file = new System.IO.StreamWriter(logPath, append = true, AutoFlush = true)
    let stopReading = new System.Threading.CancellationTokenSource()

    try
        let log = System.IO.TextWriter.Synchronized file
        log.WriteLine(sprintf "$ %s   (in %s)" command cwd)

        // Ends at end of stream, or quietly once `stopReading` fires: a reader
        // left behind must neither write to the closed log nor fault its task.
        // On the thread pool, since a read from a synchronous pipe may block.
        let pump (reader: System.IO.StreamReader) : Task =
            Task.Run(fun () ->
                task {
                    try
                        let! first = reader.ReadLineAsync(stopReading.Token)
                        let mutable line = first

                        while not (isNull line || stopReading.IsCancellationRequested) do
                            log.WriteLine line
                            let! next = reader.ReadLineAsync(stopReading.Token)
                            line <- next
                    with _ ->
                        ()
                }
                :> Task)

        let p = Process.Start(psi)
        let readers = [| pump p.StandardOutput; pump p.StandardError |]

        let outcome =
            if p.WaitForExit(timeout) then
                Exited p.ExitCode
            else
                p.Kill(entireProcessTree = true)
                p.WaitForExit()
                TimedOut timeout

        if not (Task.WaitAll(readers, grace)) then
            stopReading.Cancel()

            log.WriteLine(
                "[fssemantictagger] stopped reading output: a process the gate started still holds it open; "
                + "the log may be missing its last lines"
            )

        match outcome with
        | TimedOut _ ->
            log.WriteLine(sprintf "[fssemantictagger] killed after %dm%ds" (int timeout.TotalMinutes) timeout.Seconds)
        | Exited _ -> ()

        outcome
    finally
        stopReading.Cancel()
        file.Dispose()

/// Run `command` through `sh -c` in `cwd`, appending stdout and stderr to
/// `logPath`; kill the whole process tree once `timeout` elapses. Output still
/// unread `drainGrace` after the gate ends is dropped, with a note in the log.
let runLogged (cwd: string) (command: string) (timeout: System.TimeSpan) (logPath: string) : GateOutcome =
    runLoggedWithin drainGrace cwd command timeout logPath

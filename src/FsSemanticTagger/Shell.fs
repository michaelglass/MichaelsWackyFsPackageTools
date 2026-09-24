module FsSemanticTagger.Shell

open System.Diagnostics
open System.Threading.Tasks

type CommandResult =
    | Success of string
    | Failure of string * exitCode: int

let run (cmd: string) (args: string) : CommandResult =
    let psi = ProcessStartInfo(cmd, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
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

let runOrFail (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure(error, _) -> failwithf "%s %s failed: %s" cmd args error

let runSilent (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

/// `run` with an explicit working directory, for commands that act on another
/// repository (a consumer's workspace) rather than the one being released.
let runIn (cwd: string) (cmd: string) (args: string) : CommandResult =
    let psi = ProcessStartInfo(cmd, args)
    psi.WorkingDirectory <- cwd
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    let p = Process.Start(psi)
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

/// How a logged, time-boxed command ended: with an exit code, or killed once
/// the budget ran out.
type GateOutcome =
    | Exited of exitCode: int
    | TimedOut of budget: System.TimeSpan

/// Run `command` through `/bin/sh -c` in `cwd`, appending its interleaved
/// stdout and stderr to the file at `logPath`, and kill the whole process tree
/// once `timeout` elapses. A consumer's gate is a shell command line (`mise run
/// ci`, `./build.fsx check`) and its output belongs in a file the refusal can
/// name, not on this process's console.
let runLogged (cwd: string) (command: string) (timeout: System.TimeSpan) (logPath: string) : GateOutcome =
    let psi = ProcessStartInfo("/bin/sh")
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add command
    psi.WorkingDirectory <- cwd
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName logPath)
    |> ignore

    use log = new System.IO.StreamWriter(logPath, append = true)
    log.WriteLine(sprintf "$ %s   (in %s)" command cwd)
    log.Flush()
    let gate = obj ()

    let pump (reader: System.IO.StreamReader) =
        Task.Run(fun () ->
            let mutable line = reader.ReadLine()

            while not (isNull line) do
                lock gate (fun () ->
                    log.WriteLine line
                    log.Flush())

                line <- reader.ReadLine())

    let p = Process.Start(psi)
    let stdoutTask = pump p.StandardOutput
    let stderrTask = pump p.StandardError

    if p.WaitForExit(timeout) then
        Task.WaitAll [| stdoutTask; stderrTask |]
        Exited p.ExitCode
    else
        p.Kill(entireProcessTree = true)
        p.WaitForExit()
        Task.WaitAll [| stdoutTask; stderrTask |]
        log.WriteLine(sprintf "[fssemantictagger] killed after %dm%ds" (int timeout.TotalMinutes) timeout.Seconds)
        TimedOut timeout

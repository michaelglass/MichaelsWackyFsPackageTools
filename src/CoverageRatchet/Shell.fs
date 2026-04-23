module CoverageRatchet.Shell

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
    | Failure(output, _) -> failwithf "%s %s failed: %s" cmd args output

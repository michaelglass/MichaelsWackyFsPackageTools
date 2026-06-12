module FsSemanticTagger.Shell

open System.Diagnostics
open System.Threading.Tasks

// TODO: CommandResult diverges from CoverageRatchet.Shell, whose Failure case
// carries the process exit code (`Failure of string * exitCode: int`). Unifying
// on the exit-code-carrying shape (ideally a single linked Shell.fs) was deferred:
// no FsSemanticTagger consumer reads an exit code (every match site uses only the
// message), and switching would force ~42 mechanical edits to test construction
// sites for no behavioral gain. Revisit if FsSemanticTagger needs exit-code-aware
// branching, and unify the two Shell.fs definitions then.
type CommandResult =
    | Success of string
    | Failure of string

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

        Failure msg

let runOrFail (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure error -> failwithf "%s %s failed: %s" cmd args error

let runSilent (cmd: string) (args: string) : string option =
    match run cmd args with
    | Success output -> Some output
    | Failure _ -> None

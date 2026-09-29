module CoverageRatchet.Shell

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

let runOrFail (cmd: string) (args: string) : string =
    match run cmd args with
    | Success output -> output
    | Failure(output, _) -> failwithf "%s %s failed: %s" cmd args output

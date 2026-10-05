module Tests.Common.TestHelpers

open System
open System.IO
open System.Threading

let createTempDir () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    dir

/// Recursively delete `dir`. git objects are read-only, and Windows refuses to
/// delete read-only files, so clear the attribute and retry when that happens.
let cleanupDir dir =
    if Directory.Exists(dir) then
        try
            Directory.Delete(dir, true)
        with :? UnauthorizedAccessException ->
            for file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) do
                File.SetAttributes(file, FileAttributes.Normal)

            Directory.Delete(dir, true)

let withTempDir (action: string -> 'a) =
    let dir = createTempDir ()

    try
        action dir
    finally
        cleanupDir dir

/// Forwards writes to the capture active in the current async flow, else to `stdout`.
/// `AsyncLocal` flows into tasks, async continuations and started threads, so output from
/// work the action spawns lands in its capture while concurrent tests keep their own.
type private ConsoleRouter(stdout: TextWriter) =
    inherit TextWriter()

    static member val Capture = AsyncLocal<TextWriter>()

    member private _.Target =
        match ConsoleRouter.Capture.Value with
        | null -> stdout
        | capture -> capture

    override _.Encoding = stdout.Encoding
    override this.Write(value: char) = this.Target.Write(value)
    override this.Write(value: string) = this.Target.Write(value)
    override this.Write(buffer: char[], index: int, count: int) = this.Target.Write(buffer, index, count)
    override this.WriteLine(value: string) = this.Target.WriteLine(value)
    override this.Flush() = this.Target.Flush()

// One handle per process: on Unix each OpenStandardOutput call duplicates the descriptor.
let private standardOutput = lazy (Console.OpenStandardOutput())

/// Makes Console.Out a router that sends each write to the calling flow's capture, else to stdout.
///
/// Console.SetOut wraps the router in a synchronized writer, so a write first locks the
/// Console.Out it started on. On Unix the standard output stream then locks the current
/// Console.Out for each write. The router writes uncaptured output to its own unsynchronized
/// writer over that stream (the encoding and buffering Console gives its own stdout writer), so
/// every lock a write takes after its first is the current Console.Out, never an older one. Two
/// writes therefore cannot each hold a lock the other is waiting for, even while Console.Out is
/// being replaced.
let installConsoleRouter () =
    let stdout =
        new StreamWriter(standardOutput.Value, Console.Out.Encoding, 256, leaveOpen = true, AutoFlush = true)

    Console.SetOut(new ConsoleRouter(stdout))

// Installed by the first capture. Other tests may be writing at that moment, which is safe: see
// `installConsoleRouter`.
let private router = lazy (installConsoleRouter ())

/// Captures what `action` writes to Console.Out, isolated from concurrently running tests.
/// Captured output uses `\n` line endings on every platform (`printfn` writes `\r\n` on Windows).
let withCapturedConsole (action: unit -> 'a) : string * 'a =
    router.Force()
    let output = System.Text.StringBuilder()
    use writer = new StringWriter(output)
    let outer = ConsoleRouter.Capture.Value
    ConsoleRouter.Capture.Value <- writer

    try
        let result = action ()
        output.ToString().Replace("\r\n", "\n"), result
    finally
        ConsoleRouter.Capture.Value <- outer

/// Lay `dir` out as a jj checkout whose sources `writeSources` writes once for
/// real, at `src/Real`, and again as a copy in every place a source-tree scan
/// must skip:
///   * build output: `src/Real/bin/Debug`, `src/Real/obj`, `artifacts`, `output`,
///     `src/node_modules/pkg`
///   * a dot-directory: `src/.hidden`
///   * a nested checkout: `src/ws` (its own `.jj`), `src/wt` (a `.git` file, as a
///     git worktree has), `src/clone` (a `.git` directory)
///   * a jj workspace in the default checkout: `.workspaces/ws1`
/// `dir` itself carries a `.jj`, which must not stop it being scanned. Returns
/// the real copy's directory.
let layOutNestedCheckouts (dir: string) (writeSources: string -> unit) : string =
    let at (relative: string) =
        let path = Path.Combine(Array.append [| dir |] (relative.Split '/'))
        Directory.CreateDirectory(path) |> ignore
        path

    let copyAt (relative: string) = writeSources (at relative)

    at ".jj" |> ignore
    let real = at "src/Real"
    writeSources real

    for skipped in
        [
            "src/Real/bin/Debug"
            "src/Real/obj"
            "artifacts"
            "output"
            "src/node_modules/pkg"
            "src/.hidden"
            "src/ws"
            "src/wt"
            "src/clone"
            ".workspaces/ws1"
        ] do
        copyAt skipped

    at "src/ws/.jj" |> ignore
    at "src/clone/.git" |> ignore
    File.WriteAllText(Path.Combine(dir, "src", "wt", ".git"), "gitdir: ../../.git/worktrees/wt\n")
    at ".workspaces/ws1/.jj" |> ignore
    real

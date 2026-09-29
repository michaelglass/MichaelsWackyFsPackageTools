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

/// Forwards writes to the capture active in the current async flow, else to `fallback`.
/// `AsyncLocal` flows into tasks, async continuations and started threads, so output from
/// work the action spawns lands in its capture while concurrent tests keep their own.
type private ConsoleRouter(fallback: TextWriter) =
    inherit TextWriter()

    static member val Capture = AsyncLocal<TextWriter>()

    member private _.Target =
        match ConsoleRouter.Capture.Value with
        | null -> fallback
        | capture -> capture

    override _.Encoding = fallback.Encoding
    override this.Write(value: char) = this.Target.Write(value)
    override this.Write(value: string) = this.Target.Write(value)
    override this.Write(buffer: char[], index: int, count: int) = this.Target.Write(buffer, index, count)
    override this.WriteLine(value: string) = this.Target.WriteLine(value)
    override this.Flush() = this.Target.Flush()

let private routerLock = obj ()
let mutable private installedRouter: TextWriter = null

// Console.SetOut wraps the router in a synchronized writer, so every capture buffer is
// written under one lock. Reinstalls if something else has replaced Console.Out since.
let private ensureRouter () =
    lock routerLock (fun () ->
        if not (obj.ReferenceEquals(Console.Out, installedRouter)) then
            Console.SetOut(new ConsoleRouter(Console.Out))
            installedRouter <- Console.Out)

/// Captures what `action` writes to Console.Out, isolated from concurrently running tests.
/// Captured output uses `\n` line endings on every platform (`printfn` writes `\r\n` on Windows).
let withCapturedConsole (action: unit -> 'a) : string * 'a =
    ensureRouter ()
    let output = System.Text.StringBuilder()
    use writer = new StringWriter(output)
    let outer = ConsoleRouter.Capture.Value
    ConsoleRouter.Capture.Value <- writer

    try
        let result = action ()
        output.ToString().Replace("\r\n", "\n"), result
    finally
        ConsoleRouter.Capture.Value <- outer

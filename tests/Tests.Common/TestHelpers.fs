module Tests.Common.TestHelpers

open System
open System.IO

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

let withCapturedConsole (action: unit -> 'a) : string * 'a =
    let output = System.Text.StringBuilder()
    let writer = new StringWriter(output)
    let original = Console.Out
    Console.SetOut(writer)

    try
        let result = action ()
        writer.Flush()
        output.ToString(), result
    finally
        Console.SetOut(original)

module Tests.Common.TestHelpers

open System
open System.IO

let createTempDir () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    dir

/// Delete a temp directory, including one that contains a git repository.
///
/// `Directory.Delete(dir, true)` alone is enough on Linux and macOS and is not enough
/// on Windows, and the difference is not git behaving differently — it is the two
/// platforms disagreeing about what read-only means for deletion.
///
/// git writes loose objects mode 0444 on EVERY platform, because objects are immutable.
/// On POSIX the right to unlink comes from the parent directory's write bit and the
/// file's own mode is irrelevant, so a recursive delete walks straight through them. On
/// Windows `FILE_ATTRIBUTE_READONLY` on the file itself makes `DeleteFile` fail, so the
/// same call throws `UnauthorizedAccessException` naming a 38-hex-character path — the
/// tail of an object SHA.
///
/// Measured both ways on the same commit and the same git 2.55.0: ubuntu deletes the
/// repo and passes 81/81 of FsProjLint's suite, Windows throws and fails 7 of them, in
/// teardown, after the assertions have already passed.
///
/// The attribute walk lives in the `with` rather than ahead of the delete because the
/// overwhelming majority of temp dirs hold no read-only files at all, and enumerating
/// the tree on every teardown would charge all of them for the few that need it.
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

module SyncDocs.Tests.TestHelpers

open System
open System.IO

let createTempDir () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
    Directory.CreateDirectory(dir) |> ignore
    dir

let cleanupDir dir =
    if Directory.Exists(dir) then
        Directory.Delete(dir, true)

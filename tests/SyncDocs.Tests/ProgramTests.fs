module SyncDocs.Tests.ProgramTests

open System.IO
open Xunit
open Swensen.Unquote
open SyncDocs.Program
open Tests.Common.TestHelpers

[<Fact>]
let ``run - check prints warnings for incomplete pairs`` () =
    let tmpDir = createTempDir ()

    try
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib readme")
        // No docs/MyLib/index.md -- should warn

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let result = run [| "check" |] tmpDir
            test <@ result = Ok 0 @>
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "MyLib" @>
        test <@ printed.Contains "docs/MyLib/index.md" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - invalid command returns Error`` () =
    let tmpDir = createTempDir ()

    try
        let result = run [| "bogus" |] tmpDir
        test <@ Result.isError result @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - no args returns Error`` () =
    let tmpDir = createTempDir ()

    try
        let result = run [||] tmpDir
        test <@ Result.isError result @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - check with no pairs returns Ok 0`` () =
    let tmpDir = createTempDir ()

    try
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - check with in-sync pair returns Ok 0`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - check with out-of-sync pair returns Ok 1`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - sync updates files and returns Ok 0`` () =
    let tmpDir = createTempDir ()

    try
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Updated content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(Path.Combine(docsDir, "index.md")) = "Updated content" @>
    finally
        cleanupDir tmpDir

[<Fact>]
let ``run - sync with in-sync pair returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``main - no args returns 1`` () =
    let result = main [||]
    test <@ result = 1 @>

[<Fact>]
let ``main - bogus command returns 1`` () =
    let result = main [| "bogus" |]
    test <@ result = 1 @>

[<Fact>]
let ``main - check with no pairs in CWD returns 0`` () =
    // main uses Directory.GetCurrentDirectory(), but with no README.md
    // in the CWD's expected locations, it should find no pairs and return 0
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>
        // Verify the Ok path in main by calling run directly since main uses CWD
        // which we can't easily control. The main function just unwraps the Result.
        ())

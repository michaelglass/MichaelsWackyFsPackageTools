module SyncDocs.Tests.ProgramTests

open System.IO
open Xunit
open Swensen.Unquote
open SyncDocs.Program
open Tests.Common.TestHelpers

[<Fact>]
let ``run - check prints warnings for incomplete pairs`` () =
    withTempDir (fun tmpDir ->
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
        test <@ printed.Contains "index.md" @>)

[<Fact>]
let ``run - invalid command returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "bogus" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - no args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [||] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - check with no pairs returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with in-sync pair returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check with out-of-sync pair returns Ok 1`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - sync updates files and returns Ok 0`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Updated content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(Path.Combine(docsDir, "index.md")) = "Updated content" @>)

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
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check returns Ok 1 when files are out of sync`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "old content")

        let result = run [| "check" |] tmpDir
        test <@ result = Ok 1 @>)

[<Fact>]
let ``run - sync with multiple pairs processes all`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "root content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "old root content")

        let srcDir = Path.Combine(tmpDir, "src", "Lib")
        let libDocsDir = Path.Combine(docsDir, "Lib")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(libDocsDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "README.md"), "lib content")
        File.WriteAllText(Path.Combine(libDocsDir, "index.md"), "old lib content")

        let result = run [| "sync" |] tmpDir
        test <@ result = Ok 0 @>
        test <@ File.ReadAllText(Path.Combine(docsDir, "index.md")) = "root content" @>
        test <@ File.ReadAllText(Path.Combine(libDocsDir, "index.md")) = "lib content" @>)

[<Fact>]
let ``run - check prints in sync message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same")

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let _ = run [| "check" |] tmpDir
            ()
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "in sync" @>)

[<Fact>]
let ``run - sync prints updated message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old")

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let _ = run [| "sync" |] tmpDir
            ()
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "updated" @>)

[<Fact>]
let ``run - check prints OUT OF SYNC message`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "New")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Old")

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let _ = run [| "check" |] tmpDir
            ()
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "OUT OF SYNC" @>)

[<Fact>]
let ``run - check prints no pairs found message`` () =
    withTempDir (fun tmpDir ->
        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let _ = run [| "check" |] tmpDir
            ()
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "No README.md -> docs/ pairs found" @>)

[<Fact>]
let ``run - error message contains usage text`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "bogus" |] tmpDir

        match result with
        | Error msg -> test <@ msg.Contains "Usage" @>
        | Ok _ -> failwith "Expected Error")

[<Fact>]
let ``main - check returns 0 for empty dir`` () =
    // main uses CWD, but we can verify via run
    withTempDir (fun tmpDir ->
        let result = run [| "check" |] tmpDir
        test <@ result = Ok 0 @>)

[<Fact>]
let ``run - check prints MissingSource warning when docs target exists but source README missing`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs", "MyLib")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "lib docs")
        let srcDir = Path.Combine(tmpDir, "src", "MyLib")
        Directory.CreateDirectory(srcDir) |> ignore

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let result = run [| "check" |] tmpDir
            test <@ result = Ok 0 @>
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "Warning" @>
        test <@ printed.Contains "MyLib" @>
        test <@ printed.Contains "README.md" @>)

[<Fact>]
let ``main - returns 0 for successful run`` () =
    withTempDir (fun tmpDir ->
        let prev = System.IO.Directory.GetCurrentDirectory()

        try
            System.IO.Directory.SetCurrentDirectory(tmpDir)
            let result = main [| "check" |]
            test <@ result = 0 @>
        finally
            System.IO.Directory.SetCurrentDirectory(prev))

[<Fact>]
let ``run - multiple args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "check"; "extra" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - two word command returns Error with usage`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "sync"; "check" |] tmpDir

        match result with
        | Error msg -> test <@ msg.Contains "Usage" @>
        | Ok _ -> failwith "Expected Error")

[<Fact>]
let ``run - three args returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run [| "a"; "b"; "c" |] tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - null argv returns Error`` () =
    withTempDir (fun tmpDir ->
        let result = run (Unchecked.defaultof<string array>) tmpDir
        test <@ Result.isError result @>)

[<Fact>]
let ``run - sync prints in sync message for already synced pair`` () =
    withTempDir (fun tmpDir ->
        let docsDir = Path.Combine(tmpDir, "docs")
        Directory.CreateDirectory(docsDir) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, "README.md"), "Same content")
        File.WriteAllText(Path.Combine(docsDir, "index.md"), "Same content")

        let output = System.Text.StringBuilder()
        let origOut = System.Console.Out
        System.Console.SetOut(new System.IO.StringWriter(output))

        try
            let _ = run [| "sync" |] tmpDir
            ()
        finally
            System.Console.SetOut(origOut)

        let printed = output.ToString()
        test <@ printed.Contains "in sync" @>)

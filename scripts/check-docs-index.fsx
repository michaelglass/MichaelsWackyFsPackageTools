#!/usr/bin/env dotnet fsi

#load "../src/Shared/SourceTree.fs"
#load "../src/Shared/MsBuildProject.fs"

open System.IO

let indexPath = "docs/docs-index.html"

if not (File.Exists(indexPath)) then
    eprintfn "No %s found" indexPath
    exit 1

let indexContent = File.ReadAllText(indexPath)

// The packages the repository publishes, by the rule FsSemanticTagger and
// FsProjLint share (Shared.MsBuildProject.isPackable).
let packageIds =
    Shared.SourceTree.findFiles "." "*.fsproj"
    |> List.choose (fun fsproj ->
        match Shared.MsBuildProject.load "." fsproj with
        | Ok project when Shared.MsBuildProject.isPackable project ->
            Shared.MsBuildProject.propertyValue project "PackageId"
        | _ -> None)

let mutable failed = false

for pkg in packageIds do
    if indexContent.Contains(sprintf "href=\"%s/" pkg) then
        printfn "  PASS %s" pkg
    else
        printfn "  FAIL %s not linked in docs-index.html" pkg
        failed <- true

if failed then
    exit 1
else
    printfn "All packable projects linked in docs-index.html"

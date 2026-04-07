#!/usr/bin/env dotnet fsi

open System.IO
open System.Text.RegularExpressions

let indexPath = "docs/docs-index.html"

if not (File.Exists(indexPath)) then
    eprintfn "No %s found" indexPath
    exit 1

let indexContent = File.ReadAllText(indexPath)

let packageIdRegex = Regex(@"<PackageId>([^<]+)</PackageId>")

let fsprojFiles =
    if Directory.Exists("src") then
        Directory.GetFiles("src", "*.fsproj", SearchOption.AllDirectories)
    else
        [||]

let mutable failed = false

for fsproj in fsprojFiles do
    let content = File.ReadAllText(fsproj)
    let m = packageIdRegex.Match(content)

    if m.Success then
        let pkg = m.Groups.[1].Value

        if indexContent.Contains(sprintf "href=\"%s/" pkg) then
            printfn "  PASS %s" pkg
        else
            printfn "  FAIL %s not linked in docs-index.html" pkg
            failed <- true

if failed then
    exit 1
else
    printfn "All packable projects linked in docs-index.html"

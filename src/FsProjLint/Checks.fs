module FsProjLint.Checks

open System.IO
open System.Xml.Linq

type CheckResult =
    { Name: string
      Passed: bool
      Detail: string }

type LintResult =
    { RepoChecks: CheckResult list
      ProjectChecks: (string * CheckResult list) list }

let private fileExists (dir: string) (relativePath: string) : bool =
    File.Exists(Path.Combine(dir, relativePath))

/// Check repo-level requirements.
let checkRepo (dir: string) (hasPackableProjects: bool) : CheckResult list =
    let licenseExists = fileExists dir "LICENSE" || fileExists dir "LICENSE.md"

    let readmeExists = fileExists dir "README.md"
    let editorconfigExists = fileExists dir ".editorconfig"

    let baseChecks =
        [ { Name = "LICENSE exists"
            Passed = licenseExists
            Detail =
              if licenseExists then
                  "Found"
              else
                  "Missing LICENSE or LICENSE.md" }
          { Name = "README.md exists"
            Passed = readmeExists
            Detail = if readmeExists then "Found" else "Missing README.md" }
          { Name = ".editorconfig exists"
            Passed = editorconfigExists
            Detail =
              if editorconfigExists then
                  "Found"
              else
                  "Missing .editorconfig" } ]

    if hasPackableProjects then
        let docsIndexExists = fileExists dir "docs/index.md"

        baseChecks
        @ [ { Name = "docs/index.md exists"
              Passed = docsIndexExists
              Detail = if docsIndexExists then "Found" else "Missing docs/index.md" } ]
    else
        baseChecks

/// Get a property value from an fsproj XDocument.
let getProperty (doc: XDocument) (name: string) : string option =
    doc.Descendants(XName.Get name)
    |> Seq.tryHead
    |> Option.map (fun el -> el.Value)

/// Check if an fsproj has a PackageReference with the given package ID.
let hasPackageRef (doc: XDocument) (packageId: string) : bool =
    doc.Descendants(XName.Get "PackageReference")
    |> Seq.exists (fun el ->
        let includeAttr = el.Attribute(XName.Get "Include")

        match includeAttr with
        | null -> false
        | attr -> attr.Value = packageId)

/// Determine if a project is packable (has PackageId and IsPackable is not "false").
let isPackable (doc: XDocument) : bool =
    let hasPackageId = (getProperty doc "PackageId").IsSome

    let isPackableProp =
        match getProperty doc "IsPackable" with
        | Some "false" -> false
        | _ -> true

    hasPackageId && isPackableProp

let private checkPropertyEquals (doc: XDocument) (propName: string) (expected: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v = expected ->
        { Name = checkName
          Passed = true
          Detail = sprintf "%s is %s" propName expected }
    | Some v ->
        { Name = checkName
          Passed = false
          Detail = sprintf "%s is '%s', expected '%s'" propName v expected }
    | None ->
        { Name = checkName
          Passed = false
          Detail = sprintf "%s not found" propName }

let private checkPropertyPresent (doc: XDocument) (propName: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v.Trim().Length > 0 ->
        { Name = checkName
          Passed = true
          Detail = sprintf "%s present" propName }
    | _ ->
        { Name = checkName
          Passed = false
          Detail = sprintf "%s missing or empty" propName }

/// Check a single fsproj file.
let checkProject (_fileName: string) (doc: XDocument) : CheckResult list =
    let allProjectChecks =
        [ checkPropertyEquals doc "TreatWarningsAsErrors" "true" "TreatWarningsAsErrors is true" ]

    if isPackable doc then
        let packageChecks =
            [ checkPropertyPresent doc "Version" "Version present"
              checkPropertyPresent doc "Description" "Description present"
              checkPropertyPresent doc "Authors" "Authors present"
              checkPropertyPresent doc "PackageLicenseExpression" "PackageLicenseExpression present"
              checkPropertyPresent doc "RepositoryUrl" "RepositoryUrl present"
              checkPropertyPresent doc "RepositoryType" "RepositoryType present"
              checkPropertyEquals doc "GenerateDocumentationFile" "true" "GenerateDocumentationFile is true"
              checkPropertyEquals doc "IncludeSymbols" "true" "IncludeSymbols is true"
              checkPropertyEquals doc "SymbolPackageFormat" "snupkg" "SymbolPackageFormat is snupkg"
              (let has = hasPackageRef doc "Microsoft.SourceLink.GitHub"

               { Name = "Has Microsoft.SourceLink.GitHub"
                 Passed = has
                 Detail =
                   if has then
                       "Found"
                   else
                       "Missing Microsoft.SourceLink.GitHub PackageReference" }) ]

        allProjectChecks @ packageChecks
    else
        allProjectChecks

/// Discover all .fsproj files under the src/ directory.
let discoverProjects (dir: string) : string list =
    let srcDir = Path.Combine(dir, "src")

    if Directory.Exists(srcDir) then
        Directory.GetFiles(srcDir, "*.fsproj", SearchOption.AllDirectories)
        |> Array.toList
        |> List.sort
    else
        []

/// Run all lint checks and return a structured result.
let runLint (dir: string) : LintResult =
    let projects = discoverProjects dir

    let projectDocs = projects |> List.map (fun p -> (p, XDocument.Load(p)))

    let projectChecks =
        projectDocs
        |> List.map (fun (p, doc) -> (p, checkProject (Path.GetFileName(p)) doc))

    let hasPackable = projectDocs |> List.exists (fun (_, doc) -> isPackable doc)

    let repoChecks = checkRepo dir hasPackable

    { RepoChecks = repoChecks
      ProjectChecks = projectChecks }

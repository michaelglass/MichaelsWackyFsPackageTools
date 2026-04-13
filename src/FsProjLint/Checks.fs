module FsProjLint.Checks

open System.IO
open System.Xml.Linq

type CheckOutcome =
    | Passed
    | Failed of reason: string

module CheckOutcome =
    let isPassed =
        function
        | Passed -> true
        | Failed _ -> false

    let isFailed =
        function
        | Passed -> false
        | Failed _ -> true

type CheckResult = { Name: string; Outcome: CheckOutcome }

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
            Outcome =
              if licenseExists then
                  Passed
              else
                  Failed "Missing LICENSE or LICENSE.md" }
          { Name = "README.md exists"
            Outcome = if readmeExists then Passed else Failed "Missing README.md" }
          { Name = ".editorconfig exists"
            Outcome =
              if editorconfigExists then
                  Passed
              else
                  Failed "Missing .editorconfig" } ]

    if hasPackableProjects then
        let docsIndexExists = fileExists dir "docs/index.md"

        baseChecks
        @ [ { Name = "docs/index.md exists"
              Outcome =
                if docsIndexExists then
                    Passed
                else
                    Failed "Missing docs/index.md" } ]
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
    | Some v when v = expected -> { Name = checkName; Outcome = Passed }
    | Some v ->
        { Name = checkName
          Outcome = Failed(sprintf "%s is '%s', expected '%s'" propName v expected) }
    | None ->
        { Name = checkName
          Outcome = Failed(sprintf "%s not found" propName) }

let private checkPropertyPresent (doc: XDocument) (propName: string) (checkName: string) =
    match getProperty doc propName with
    | Some v when v.Trim().Length > 0 -> { Name = checkName; Outcome = Passed }
    | _ ->
        { Name = checkName
          Outcome = Failed(sprintf "%s missing or empty" propName) }

/// Check a single fsproj file.
let checkProject (doc: XDocument) : CheckResult list =
    let allProjectChecks =
        [ checkPropertyEquals doc "TreatWarningsAsErrors" "true" "TreatWarningsAsErrors is true" ]

    if isPackable doc then
        let includesBuildOutput = getProperty doc "IncludeBuildOutput" <> Some "false"

        let packageChecks =
            [ checkPropertyPresent doc "Version" "Version present"
              checkPropertyPresent doc "Description" "Description present"
              checkPropertyPresent doc "Authors" "Authors present"
              checkPropertyPresent doc "PackageLicenseExpression" "PackageLicenseExpression present"
              checkPropertyPresent doc "RepositoryUrl" "RepositoryUrl present"
              checkPropertyPresent doc "RepositoryType" "RepositoryType present"
              checkPropertyEquals doc "GenerateDocumentationFile" "true" "GenerateDocumentationFile is true"
              (let has = hasPackageRef doc "Microsoft.SourceLink.GitHub"

               { Name = "Has Microsoft.SourceLink.GitHub"
                 Outcome =
                   if has then
                       Passed
                   else
                       Failed "Missing Microsoft.SourceLink.GitHub PackageReference" }) ]

        let symbolChecks =
            if includesBuildOutput then
                [ checkPropertyEquals doc "IncludeSymbols" "true" "IncludeSymbols is true"
                  checkPropertyEquals doc "SymbolPackageFormat" "snupkg" "SymbolPackageFormat is snupkg" ]
            else
                []

        allProjectChecks @ packageChecks @ symbolChecks
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

    let loadResults =
        projects
        |> List.map (fun p ->
            try
                let doc = XDocument.Load(p)
                (p, Ok doc)
            with ex ->
                (p, Error ex.Message))

    let projectChecks =
        loadResults
        |> List.map (fun (p, result) ->
            match result with
            | Ok doc -> (p, checkProject doc)
            | Error msg ->
                (p,
                 [ { Name = "XML parse"
                     Outcome = Failed(sprintf "Failed to parse %s: %s" (Path.GetFileName(p)) msg) } ]))

    let hasPackable =
        loadResults
        |> List.exists (fun (_, result) ->
            match result with
            | Ok doc -> isPackable doc
            | Error _ -> false)

    let repoChecks = checkRepo dir hasPackable

    { RepoChecks = repoChecks
      ProjectChecks = projectChecks }

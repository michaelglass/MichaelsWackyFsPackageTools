/// An .fsproj together with the Directory.Build.props MSBuild imports into it,
/// and the rule for which projects a repository publishes as packages.
///
/// Compiled into FsSemanticTagger and FsProjLint through linked
/// <Compile Include="../Shared/MsBuildProject.fs" Link="MsBuildProject.fs" />
/// items, so both tools read project properties and decide packability the same
/// way. See GitDir.fs for why this is not its own project.
module Shared.MsBuildProject

open System
open System.IO
open System.Xml.Linq

/// A property value and the file that sets it.
type internal PropertySource = { Value: string; File: string }

/// A parsed project file and the nearest Directory.Build.props, the file MSBuild
/// imports before the project's own properties.
[<NoComparison>]
type internal Project =
    {
        /// Full path of the project file.
        Path: string
        Document: XDocument
        /// Full path and content of the nearest Directory.Build.props at or above
        /// the project's directory, stopping at the repository root.
        DirectoryBuildProps: (string * XDocument) option
    }

let private declared (doc: XDocument) (name: string) : string option =
    doc.Descendants(XName.Get name)
    |> Seq.tryHead
    |> Option.map (fun el -> el.Value.Trim())

/// The value MSBuild sees for property `name`, and the file it comes from: the
/// project's own declaration, else the nearest Directory.Build.props's. None when
/// the value is empty. A declaration in the project wins even when empty, as in
/// MSBuild, so an empty one hides the props file's value. MSBuild
/// imports only the nearest props file, so a property it lacks is unset even when
/// a props file further up sets it (unless the nearest one imports that file,
/// which this does not follow). Conditions are not evaluated.
let internal property (project: Project) (name: string) : PropertySource option =
    let source =
        match declared project.Document name with
        | Some value -> Some { Value = value; File = project.Path }
        | None ->
            project.DirectoryBuildProps
            |> Option.bind (fun (file, doc) ->
                declared doc name |> Option.map (fun value -> { Value = value; File = file }))

    source |> Option.filter (fun found -> found.Value.Length > 0)

/// The value MSBuild sees for property `name` (see `property`).
let internal propertyValue (project: Project) (name: string) : string option =
    property project name |> Option.map (fun found -> found.Value)

/// Is this project published as a package? It has a PackageId, is not
/// `IsPackable` false (test projects), and is not an example app: an
/// `OutputType` Exe without `PackAsTool` true is something to run, not to publish,
/// so benchmarks and samples are left out while dotnet tools are kept.
let internal isPackable (project: Project) : bool =
    let is name expected =
        propertyValue project name
        |> Option.exists (fun value -> String.Equals(value, expected, StringComparison.OrdinalIgnoreCase))

    (propertyValue project "PackageId").IsSome
    && not (is "IsPackable" "false")
    && not (is "OutputType" "Exe" && not (is "PackAsTool" "true"))

/// The nearest Directory.Build.props at or above `projectDir`, stopping at
/// `repoDir`: the project's directory first, then each parent up to the root.
let private nearestDirectoryBuildProps (repoDir: string) (projectDir: string) : string option =
    let root = Path.GetFullPath repoDir

    // root, root/src, root/src/Lib, ... then nearest first.
    Path
        .GetRelativePath(root, projectDir)
        .Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter (fun segment -> segment <> ".")
    |> Array.scan (fun dir segment -> Path.Combine(dir, segment)) root
    |> Array.rev
    |> Array.map (fun dir -> Path.Combine(dir, "Directory.Build.props"))
    |> Array.tryFind File.Exists

/// Parse the project at `projectPath` and the nearest Directory.Build.props up to
/// `repoDir`. Error names the file that does not parse.
let internal load (repoDir: string) (projectPath: string) : Result<Project, string> =
    let parse (file: string) =
        try
            Ok(XDocument.Load file)
        with ex ->
            Error(sprintf "Failed to parse %s: %s" (Path.GetFileName file) ex.Message)

    let projectPath = Path.GetFullPath projectPath

    let directoryBuildProps =
        match nearestDirectoryBuildProps repoDir (Path.GetDirectoryName projectPath) with
        | None -> Ok None
        | Some props -> parse props |> Result.map (fun doc -> Some(props, doc))

    parse projectPath
    |> Result.bind (fun document ->
        directoryBuildProps
        |> Result.map (fun props ->
            {
                Path = projectPath
                Document = document
                DirectoryBuildProps = props
            }))

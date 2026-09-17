module FsSemanticTagger.Tests.ReleaseOrderTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger
open FsSemanticTagger.Config
open Tests.Common.TestHelpers

/// A reference graph over fsprojs, for driving the pure `build` without a disk.
let private reaching (edges: (string * string list) list) (fsproj: string) : string list =
    edges
    |> List.tryFind (fun (from, _) -> from = fsproj)
    |> Option.map snd
    |> Option.defaultValue []

let private graphOf packages edges =
    match ReleaseOrder.build packages (reaching edges) with
    | Ok graph -> graph
    | Error reason -> failwithf "expected a graph, got: %s" reason

let private errorOf packages edges =
    match ReleaseOrder.build packages (reaching edges) with
    | Ok _ -> failwith "expected the graph to be refused"
    | Error reason -> reason

let private wavesOf graph names = ReleaseOrder.waves graph id names

[<Fact>]
let ``a dependency released with its dependent is published first, even when listed after it`` () =
    // The FsHotWatch shape: the CLI is listed first and references TestPrune.
    let graph =
        graphOf
            [ "Cli", [ "src/Cli/Cli.fsproj" ]
              "TestPrune", [ "src/TestPrune/TestPrune.fsproj" ] ]
            [ "src/Cli/Cli.fsproj", [ "src/TestPrune/TestPrune.fsproj" ] ]

    test <@ wavesOf graph [ "Cli"; "TestPrune" ] = [ [ "TestPrune" ]; [ "Cli" ] ] @>

[<Fact>]
let ``unrelated packages share one wave in their existing order (positive control)`` () =
    let graph =
        graphOf
            [ "Zeta", [ "src/Zeta/Zeta.fsproj" ]
              "Alpha", [ "src/Alpha/Alpha.fsproj" ]
              "Mid", [ "src/Mid/Mid.fsproj" ] ]
            []

    test <@ wavesOf graph [ "Zeta"; "Alpha"; "Mid" ] = [ [ "Zeta"; "Alpha"; "Mid" ] ] @>

[<Fact>]
let ``independent branches share a wave while each dependent waits for its own dependency`` () =
    let graph =
        graphOf
            [ "AppA", [ "a/AppA.fsproj" ]
              "LibA", [ "a/LibA.fsproj" ]
              "AppB", [ "b/AppB.fsproj" ]
              "LibB", [ "b/LibB.fsproj" ] ]
            [ "a/AppA.fsproj", [ "a/LibA.fsproj" ]; "b/AppB.fsproj", [ "b/LibB.fsproj" ] ]

    test <@ wavesOf graph [ "AppA"; "LibA"; "AppB"; "LibB" ] = [ [ "LibA"; "LibB" ]; [ "AppA"; "AppB" ] ] @>

[<Fact>]
let ``a chain publishes one link per wave, and a diamond's apex waits for both sides`` () =
    let chain =
        graphOf
            [ "A", [ "A.fsproj" ]; "B", [ "B.fsproj" ]; "C", [ "C.fsproj" ] ]
            [ "A.fsproj", [ "B.fsproj"; "C.fsproj" ]; "B.fsproj", [ "C.fsproj" ] ]

    test <@ wavesOf chain [ "A"; "B"; "C" ] = [ [ "C" ]; [ "B" ]; [ "A" ] ] @>

    let diamond =
        graphOf
            [ "Top", [ "Top.fsproj" ]
              "Left", [ "L.fsproj" ]
              "Right", [ "R.fsproj" ]
              "Base", [ "Base.fsproj" ] ]
            [ "Top.fsproj", [ "L.fsproj"; "R.fsproj"; "Base.fsproj" ]
              "L.fsproj", [ "Base.fsproj" ]
              "R.fsproj", [ "Base.fsproj" ] ]

    test <@ wavesOf diamond [ "Top"; "Left"; "Right"; "Base" ] = [ [ "Base" ]; [ "Left"; "Right" ]; [ "Top" ] ] @>

[<Fact>]
let ``a dependency reached through a package that is not being released still orders the release`` () =
    // A -> B -> C at the package level (a library's fsproj reaches only B). B is not
    // part of this release, but A was built against a C that must be out first.
    let graph =
        graphOf
            [ "A", [ "A.fsproj" ]; "B", [ "B.fsproj" ]; "C", [ "C.fsproj" ] ]
            [ "A.fsproj", [ "B.fsproj" ]; "B.fsproj", [ "C.fsproj" ] ]

    test <@ ReleaseOrder.dependenciesOf graph "A" = set [ "B"; "C" ] @>
    test <@ wavesOf graph [ "A"; "C" ] = [ [ "C" ]; [ "A" ] ] @>

[<Fact>]
let ``a dependency that is not being released imposes no wait`` () =
    let graph =
        graphOf [ "App", [ "App.fsproj" ]; "Lib", [ "Lib.fsproj" ] ] [ "App.fsproj", [ "Lib.fsproj" ] ]

    test <@ wavesOf graph [ "App" ] = [ [ "App" ] ] @>
    test <@ List.isEmpty (wavesOf graph []) @>

[<Fact>]
let ``a name the graph does not know depends on nothing`` () =
    let graph = graphOf [ "Known", [ "Known.fsproj" ] ] []

    test <@ ReleaseOrder.dependenciesOf graph "Stranger" = Set.empty @>
    test <@ wavesOf graph [ "Stranger"; "Known" ] = [ [ "Stranger"; "Known" ] ] @>

[<Fact>]
let ``an fsproj that is neither a package nor reaches one adds no edge, and a package's own fsprojs are not dependencies``
    ()
    =
    let graph =
        graphOf
            [ "Tool", [ "Tool.fsproj"; "Tool.Plugin.fsproj" ]; "Lib", [ "Lib.fsproj" ] ]
            [ "Tool.fsproj", [ "Helper.fsproj"; "Tool.Plugin.fsproj" ]
              "Tool.Plugin.fsproj", [ "Tool.Plugin.fsproj" ] ]

    test <@ ReleaseOrder.dependenciesOf graph "Tool" = Set.empty @>
    test <@ wavesOf graph [ "Tool"; "Lib" ] = [ [ "Tool"; "Lib" ] ] @>

[<Fact>]
let ``a cycle is refused, naming the loop`` () =
    let reason =
        errorOf [ "A", [ "A.fsproj" ]; "B", [ "B.fsproj" ] ] [ "A.fsproj", [ "B.fsproj" ]; "B.fsproj", [ "A.fsproj" ] ]

    test <@ reason.Contains "cycle (A -> B -> A)" @>

[<Fact>]
let ``a cycle made only by packages that release several fsprojs under one tag is refused`` () =
    // No fsproj references itself; the loop exists only once fsprojs are grouped
    // into packages: One = {one, one.extra}; one -> two; two -> one.extra.
    let reason =
        errorOf
            [ "One", [ "one.fsproj"; "one.extra.fsproj" ]; "Two", [ "two.fsproj" ] ]
            [ "one.fsproj", [ "two.fsproj" ]; "two.fsproj", [ "one.extra.fsproj" ] ]

    test <@ reason.Contains "cycle (One -> Two -> One)" @>

[<Fact>]
let ``a longer cycle names only the packages on the loop`` () =
    let reason =
        errorOf
            [ "Entry", [ "e.fsproj" ]
              "X", [ "x.fsproj" ]
              "Y", [ "y.fsproj" ]
              "Z", [ "z.fsproj" ] ]
            [ "e.fsproj", [ "x.fsproj" ]
              "x.fsproj", [ "y.fsproj" ]
              "y.fsproj", [ "z.fsproj" ]
              "z.fsproj", [ "x.fsproj" ] ]

    test <@ reason.Contains "cycle (X -> Y -> Z -> X)" @>

[<Fact>]
let ``an fsproj claimed by two packages is refused as ambiguous`` () =
    let reason =
        errorOf [ "A", [ "a.fsproj"; "shared.fsproj" ]; "B", [ "b.fsproj"; "shared.fsproj" ] ] []

    test <@ reason.Contains "shared.fsproj is claimed by A and B" @>

[<Fact>]
let ``two packages with one name are refused`` () =
    let reason = errorOf [ "Twin", [ "a.fsproj" ]; "Twin", [ "b.fsproj" ] ] []

    test <@ reason.Contains "more than one package Twin" @>

[<Fact>]
let ``fromConfig orders the FsHotWatch shape from the fsprojs on disk`` () =
    withTempDir (fun root ->
        let write (relPath: string) (body: string) =
            let full = Path.Combine(root, relPath)
            Directory.CreateDirectory(Path.GetDirectoryName(full)) |> ignore
            File.WriteAllText(full, sprintf "<Project Sdk=\"Microsoft.NET.Sdk\">%s</Project>" body)

        // A PackAsTool CLI, listed FIRST, bundling a separately released TestPrune.
        write
            "src/Cli/Cli.fsproj"
            "<PropertyGroup><PackAsTool>true</PackAsTool></PropertyGroup><ItemGroup><ProjectReference Include=\"../TestPrune/TestPrune.fsproj\" /></ItemGroup>"

        write
            "src/TestPrune/TestPrune.fsproj"
            "<ItemGroup><ProjectReference Include=\"../Core/Core.fsproj\" /></ItemGroup>"

        write "src/Core/Core.fsproj" ""

        let package name fsproj =
            { Name = name
              Fsproj = fsproj
              DllPath = ""
              TagPrefix = name + "-v"
              FsProjsSharingSameTag = [] }

        let config =
            { Packages =
                [ package "Cli" "src/Cli/Cli.fsproj"
                  // Absolute, as a test config or a hand-written one may spell it.
                  package "TestPrune" (Path.Combine(root, "src", "TestPrune", "TestPrune.fsproj"))
                  package "Core" "src/Core/Core.fsproj" ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              PublishWorkflows = defaultPublishWorkflows
              RootDir = root }

        match ReleaseOrder.fromConfig config with
        | Error reason -> failwithf "expected a graph, got: %s" reason
        | Ok graph ->
            test <@ ReleaseOrder.dependenciesOf graph "Cli" = set [ "TestPrune"; "Core" ] @>
            test <@ wavesOf graph [ "Cli"; "TestPrune" ] = [ [ "TestPrune" ]; [ "Cli" ] ] @>
            test <@ wavesOf graph [ "Cli"; "TestPrune"; "Core" ] = [ [ "Core" ]; [ "TestPrune" ]; [ "Cli" ] ] @>)

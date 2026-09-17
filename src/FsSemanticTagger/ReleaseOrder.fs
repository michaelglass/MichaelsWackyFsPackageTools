/// The order a multi-package release publishes in.
///
/// Every package is released by its own tag, and each tag triggers its own workflow
/// run. Nothing in those runs orders them, so a package whose `<ProjectReference>`
/// closure contains another separately released package can reach the feed before
/// the dependency it was built against. The release therefore publishes in WAVES: a
/// package is published only after every separately released package it depends on
/// that is part of the same release. Packages with no dependency on each other share
/// a wave and are pushed together.
///
/// The decisions here are pure. Reading fsprojs is confined to `fromConfig`.
module FsSemanticTagger.ReleaseOrder

open FsSemanticTagger.Config

/// The dependency graph of a repo's separately released packages, keyed by package
/// name. It can only be built by `build`, which refuses cycles and ambiguous
/// ownership, so every graph that exists can be ordered.
type ReleaseGraph =
    private
        {
            /// Package name -> every package it depends on, directly or transitively.
            DependsOn: Map<string, Set<string>>
        }

/// The packages `name` depends on, directly or transitively. Empty for a name the
/// graph does not know.
let dependenciesOf (graph: ReleaseGraph) (name: string) : Set<string> =
    graph.DependsOn |> Map.tryFind name |> Option.defaultValue Set.empty

/// Close `direct` transitively, or return the first cycle found as the path of names
/// that leads back to where it started (`A -> B -> A` is `["A"; "B"; "A"]`).
let private transitiveClosure (direct: Map<string, Set<string>>) : Result<Map<string, Set<string>>, string list> =
    // `path` holds the names being visited, innermost first.
    let rec visit (path: string list) (closed: Map<string, Set<string>>) (name: string) =
        if closed.ContainsKey name then
            Ok closed
        elif List.contains name path then
            let loop = path |> List.takeWhile (fun visiting -> visiting <> name) |> List.rev
            Error((name :: loop) @ [ name ])
        else
            // Every dependency is itself a package, so every name visited is a key.
            let deps = direct[name]

            deps
            |> Set.fold
                (fun acc dep -> acc |> Result.bind (fun closedSoFar -> visit (name :: path) closedSoFar dep))
                (Ok closed)
            |> Result.map (fun closedAfter ->
                let reachable =
                    deps |> Seq.fold (fun all dep -> Set.union all closedAfter[dep]) deps

                closedAfter.Add(name, reachable))

    direct
    |> Map.fold (fun acc name _ -> acc |> Result.bind (fun closed -> visit [] closed name)) (Ok Map.empty)

/// Build the graph from each package's name and fsprojs (its own plus any released
/// under the same tag), given `reachableFsprojs`, which lists every fsproj a given
/// fsproj reaches through `<ProjectReference>`. Fsproj paths on both sides must use
/// the same spelling (repo-root-relative, forward slashes).
///
/// A package depends on another when any of its fsprojs reaches any of the other's.
/// Fails, naming the problem, when two packages share a name, when one fsproj is
/// claimed by two packages (it could not be said which release it belongs to), or
/// when the dependencies form a cycle (no order publishes every dependency first).
let build
    (packages: (string * string list) list)
    (reachableFsprojs: string -> string list)
    : Result<ReleaseGraph, string> =
    let duplicateNames =
        packages
        |> List.countBy fst
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map fst

    let owners =
        packages
        |> List.collect (fun (name, fsprojs) -> fsprojs |> List.distinct |> List.map (fun fsproj -> fsproj, name))
        |> List.groupBy fst
        |> List.map (fun (fsproj, claims) -> fsproj, claims |> List.map snd)

    let contested = owners |> List.filter (fun (_, names) -> List.length names > 1)

    if not (List.isEmpty duplicateNames) then
        Error(
            sprintf
                "semantic-tagger.json names more than one package %s; the release order cannot tell them apart."
                (String.concat ", " duplicateNames)
        )
    elif not (List.isEmpty contested) then
        let describe (fsproj, names) =
            sprintf "%s is claimed by %s" fsproj (String.concat " and " names)

        Error(
            sprintf
                "an fsproj belongs to more than one package, so the release order is ambiguous: %s."
                (contested |> List.map describe |> String.concat "; ")
        )
    else
        let ownerOf =
            owners
            |> List.map (fun (fsproj, names) -> fsproj, List.head names)
            |> Map.ofList

        let direct =
            packages
            |> List.map (fun (name, fsprojs) ->
                let deps =
                    fsprojs
                    |> List.collect reachableFsprojs
                    |> List.choose ownerOf.TryFind
                    |> List.filter (fun dep -> dep <> name)
                    |> Set.ofList

                name, deps)
            |> Map.ofList

        match transitiveClosure direct with
        | Ok closed -> Ok { DependsOn = closed }
        | Error cycle ->
            Error(
                sprintf
                    "the separately released packages depend on each other in a cycle (%s), so no release order publishes every dependency first."
                    (String.concat " -> " cycle)
            )

/// Group `items` into publication waves. An item is placed in the wave after the
/// latest wave holding something it depends on, so every dependency that is part of
/// this release is published in an earlier wave than its dependents. Items that do not
/// depend on each other share a wave, and each wave keeps the items in their given
/// order. Dependencies that are not among `items` (not being released now) impose no
/// wait, but a dependency reached THROUGH one still does, because the graph is
/// transitive.
let waves (graph: ReleaseGraph) (nameOf: 'T -> string) (items: 'T list) : 'T list list =
    let releasing = items |> List.map nameOf |> Set.ofList

    // Terminates because the graph is acyclic: `build` refuses anything else.
    let rec depth (name: string) : int =
        dependenciesOf graph name
        |> Set.intersect releasing
        |> Seq.fold (fun deepest dep -> max deepest (depth dep + 1)) 0

    items
    |> List.map (fun item -> depth (nameOf item), item)
    |> List.groupBy fst
    |> List.sortBy fst
    |> List.map (fun (_, placed) -> placed |> List.map snd)

/// The graph for `config`'s packages, derived from the `<ProjectReference>` items on
/// disk. Each package contributes its own fsproj and its `fsProjsSharingSameTag`.
let fromConfig (config: ToolConfig) : Result<ReleaseGraph, string> =
    let relative = repoRelativeFsproj config.RootDir

    let packages =
        config.Packages
        |> List.map (fun pkg -> pkg.Name, (pkg.Fsproj :: pkg.FsProjsSharingSameTag) |> List.map relative)

    build packages (transitiveProjectRefFsprojs config.RootDir)

/// Stand-ins for the `dotnet restore` of the availability probe that leave on disk
/// what the real command leaves: the probe's `obj/project.assets.json`, and, under
/// the `--packages` folder when one is passed, the restored package's
/// `.nupkg.metadata`, which names the feed the package came from.
module FsSemanticTagger.Tests.RestoreProbeFakes

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FsSemanticTagger

let nugetOrg = "https://api.nuget.org/v3/index.json"

let private quotedAfter (flag: string) (args: string) : string option =
    let m = Regex.Match(args, Regex.Escape flag + " \"([^\"]*)\"")
    if m.Success then Some m.Groups[1].Value else None

/// A `dotnet restore` that exits 0 having resolved `packageId` at `resolvedVersion`
/// from `source`, printing `output`. A resolved version other than the requested
/// one is what NuGet's nearest-version rule (NU1603) produces.
let restoreResolving
    (packageId: string)
    (resolvedVersion: string)
    (source: string)
    (output: string)
    : string -> string -> Shell.CommandResult =
    fun _cmd args ->
        let proj = Regex.Match(args, "^restore \"([^\"]*)\"").Groups[1].Value
        let obj = Path.Combine(Path.GetDirectoryName proj, "obj")
        Directory.CreateDirectory obj |> ignore

        let assets =
            sprintf
                """{"version":3,"libraries":{%s:{"type":"package"}},"project":{"restore":{"sources":{%s:{}}},"frameworks":{"net10.0":{"runtimeIdentifierGraphPath":"/dotnet/sdk/10.0.400/PortableRuntimeIdentifierGraph.json"}}}}"""
                (JsonSerializer.Serialize(packageId + "/" + resolvedVersion))
                (JsonSerializer.Serialize source)

        File.WriteAllText(Path.Combine(obj, "project.assets.json"), assets)

        match quotedAfter "--packages" args with
        | Some packages ->
            let dir =
                Path.Combine(packages, packageId.ToLowerInvariant(), resolvedVersion.ToLowerInvariant())

            Directory.CreateDirectory dir |> ignore

            File.WriteAllText(
                Path.Combine(dir, ".nupkg.metadata"),
                sprintf """{"version":2,"contentHash":"x","source":%s}""" (JsonSerializer.Serialize source)
            )
        | None -> ()

        Shell.Success output

/// The restore FsHotWatch's release gate saw for its core package: the requested
/// version was not on any feed yet, and NuGet resolved a ref-stamped build that a
/// local proof run had left in the global packages folder, because a prerelease
/// label `alpha.30-ref.…` sorts above every `alpha.<n>`. Exit code 0.
let restoreResolvingNearest (packageId: string) (requested: string) (substitute: string) =
    restoreResolving
        packageId
        substitute
        "/private/tmp/proof-packages"
        (sprintf
            "  Determining projects to restore...\nwarning NU1603: probe depends on %s (>= %s) but %s %s was not found. %s %s was resolved instead.\n  Restored probe.csproj"
            packageId
            requested
            packageId
            requested
            packageId
            substitute)

/// AUTOMATION-822: two copies of this suite run at once on one machine (two
/// workspaces gating together) share the system temp dir. A fixture at a fixed path
/// directly in it is shared between them, and one run's writes become the other's
/// flake. Every fixture root is a directory of its own.
module FsSemanticTagger.Tests.TempIsolationTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger

let private sharedTemp =
    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))

let private pathsOf (settings: ConsumerCanary.Settings) =
    [
        settings.ConfigPath
        settings.LogDir
        settings.ScratchDir
        settings.PackagesCache
        settings.VerdictDir
    ]

let private directlyInSharedTemp (path: string) =
    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath path)) = sharedTemp

[<Fact>]
let ``the release suites' canary fixtures sit under a root of their own, never directly in the shared temp dir`` () =
    let shared =
        [ OrphanedBumpTests.noCanary; ReleaseWavesTests.noCanary ]
        |> List.collect pathsOf
        |> List.filter directlyInSharedTemp

    test <@ List.isEmpty shared @>

[<Fact>]
let ``two canary fixtures never share a path`` () =
    Tests.Common.TestHelpers.withTempDir (fun first ->
        Tests.Common.TestHelpers.withTempDir (fun second ->
            let shared =
                Set.intersect
                    (set (pathsOf (CanaryFixtures.noCanaryUnder first)))
                    (set (pathsOf (CanaryFixtures.noCanaryUnder second)))

            test <@ Set.isEmpty shared @>))

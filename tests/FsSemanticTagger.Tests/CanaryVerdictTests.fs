/// The consumer canary's recorded verdict, and when a resumed release may reuse it.
///
/// Observed 2026-11-05 in the FsHotWatch release: the canary passed on the pre-bump
/// tree, the CI wait on the pushed bump commit gave up, and the resume re-ran the
/// canary on the post-bump tree. That tree's CLI bundles a dependency the same
/// release had not published yet, so the canary's restore failed (NU1102) and the
/// release could never finish, although the bump commit changes nothing but
/// versions and changelogs.
module FsSemanticTagger.Tests.CanaryVerdictTests

open System
open System.IO
open Xunit
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger.CanaryVerdict

let private plan = [ "FsHotWatch.Cli 0.14.0-alpha.138"; "TestPrune.Core 15.0.0" ]

let private consumers =
    [ "FsHotWatch.Cli ~/intelligence .config/dotnet-tools.json ./build.fsx check" ]

let private recorded (outcome: Outcome) (content: string) : Record =
    {
        Plan = plan
        Consumers = consumers
        Content = content
        Outcome = outcome
        RecordedAt = DateTimeOffset(2026, 11, 5, 4, 0, 0, TimeSpan.Zero)
    }

let private neverAsked (_: string) : Result<unit, string> =
    failwith "the bump-only check must not run"

// ---- the store ----

[<Fact>]
let ``save then load - the record comes back for the same plan and consumers`` () =
    withTempDir (fun dir ->
        let record = recorded CanaryPassed "base111"
        save dir record
        test <@ load dir plan consumers = Some record @>)

[<Fact>]
let ``load - a different plan or a different consumer set finds nothing`` () =
    withTempDir (fun dir ->
        save dir (recorded CanaryPassed "base111")
        test <@ load dir [ "FsHotWatch.Cli 0.14.0-alpha.139" ] consumers = None @>
        test <@ load dir plan [ "FsHotWatch.Cli ~/elsewhere pin gate" ] = None @>
        test <@ fileFor dir plan consumers <> fileFor dir [ "Other 1.0.0" ] consumers @>)

[<Fact>]
let ``load - the newest verdict for a plan replaces the older one`` () =
    withTempDir (fun dir ->
        save dir (recorded CanaryPassed "base111")
        save dir (recorded CanaryRefused "base111")
        test <@ (load dir plan consumers |> Option.map _.Outcome) = Some CanaryRefused @>)

[<Fact>]
let ``load - an unreadable record is no record`` () =
    withTempDir (fun dir ->
        let path = fileFor dir plan consumers
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(path, "{ not json")
        test <@ load dir plan consumers = None @>

        File.WriteAllText(
            path,
            """{"plan":[],"consumers":[],"content":"x","outcome":"maybe","recordedAt":"2026-11-05T04:00:00Z"}"""
        )

        test <@ load dir plan consumers = None @>
        File.WriteAllText(path, "null")
        test <@ load dir plan consumers = None @>)

[<Fact>]
let ``load - a record in the plan's file that names another plan or other consumers is no record`` () =
    withTempDir (fun dir ->
        let path = fileFor dir plan consumers
        Directory.CreateDirectory dir |> ignore

        File.WriteAllText(
            path,
            toJson
                { recorded CanaryPassed "base111" with
                    Plan = [ "Other 1.0.0" ]
                }
        )

        test <@ load dir plan consumers = None @>

        File.WriteAllText(
            path,
            toJson
                { recorded CanaryPassed "base111" with
                    Consumers = [ "Other ~/elsewhere pin gate" ]
                }
        )

        test <@ load dir plan consumers = None @>)

[<Fact>]
let ``toJson then parse - round-trips both outcomes`` () =
    for outcome in [ CanaryPassed; CanaryRefused ] do
        let record = recorded outcome "base111"
        test <@ parse (toJson record) = Some record @>

// ---- reuse ----

[<Fact>]
let ``decide - no record means the canary runs`` () =
    test
        <@
            match decide None "bump222" neverAsked with
            | NotReusable _ -> true
            | Reusable _ -> false
        @>

[<Fact>]
let ``decide - a refused verdict is never reused, even on identical content`` () =
    test
        <@
            match decide (Some(recorded CanaryRefused "bump222")) "bump222" neverAsked with
            | NotReusable reason -> reason.Contains "refused"
            | Reusable _ -> false
        @>

[<Fact>]
let ``decide - a pass on the very same commit is reused without comparing anything`` () =
    let record = recorded CanaryPassed "base111"
    test <@ decide (Some record) "base111" neverAsked = Reusable record @>

[<Fact>]
let ``decide - a pass is reused when the only change since is the release's own bump`` () =
    let record = recorded CanaryPassed "base111"

    let bumpOnly (from: string) =
        if from = "base111" then
            Ok()
        else
            Error "asked about the wrong commit"

    test <@ decide (Some record) "bump222" bumpOnly = Reusable record @>

[<Fact>]
let ``decide - a pass on different content is not reused, and the reason is kept`` () =
    let record = recorded CanaryPassed "base111"

    test
        <@
            decide (Some record) "bump222" (fun _ -> Error "src/App/App.fs changed since the canary ran") =
                NotReusable "src/App/App.fs changed since the canary ran"
        @>

// ---- what counts as the release's own bump ----

let private project (version: string) (extra: string) =
    sprintf "<Project>\n  <PropertyGroup>\n    <Version>%s</Version>%s\n  </PropertyGroup>\n</Project>\n" version extra

[<Fact>]
let ``onlyVersionDiffers - a Version change alone is a bump, anything else is not`` () =
    test <@ onlyVersionDiffers (project "1.0.0" "") (project "1.1.0" "") @>
    test <@ onlyVersionDiffers (project "1.0.0" "") (project "1.0.0" "") @>
    test <@ not (onlyVersionDiffers (project "1.0.0" "") (project "1.1.0" "<IsPackable>true</IsPackable>")) @>

let private files: Map<string * string, string> =
    Map.ofList
        [
            ("base111", "src/Cli/Cli.fsproj"), project "0.14.0-alpha.137" ""
            ("bump222", "src/Cli/Cli.fsproj"), project "0.14.0-alpha.138" ""
            ("base111", "src/Core/Core.fsproj"), project "14.0.0" ""
            ("bump222", "src/Core/Core.fsproj"), project "15.0.0" "\n    <NoWarn>FS0044</NoWarn>"
        ]

let private fileAt (rev: string) (path: string) : string option = files.TryFind((rev, path))

let private fsprojs = set [ "src/Cli/Cli.fsproj"; "src/Core/Core.fsproj" ]
let private changelogs = set [ "src/Cli/CHANGELOG.md"; "src/Core/CHANGELOG.md" ]

[<Fact>]
let ``bumpOnly - version-only fsproj edits and changelogs are the release's own bump`` () =
    test
        <@
            bumpOnly fsprojs changelogs [ "src/Cli/Cli.fsproj"; "src/Cli/CHANGELOG.md" ] fileAt "base111" "bump222" =
                Ok()
        @>

[<Fact>]
let ``bumpOnly - backslashed paths are compared as forward-slashed`` () =
    test <@ bumpOnly fsprojs changelogs [ "src\\Cli\\CHANGELOG.md" ] fileAt "base111" "bump222" = Ok() @>

[<Fact>]
let ``bumpOnly - a file outside the plan's fsprojs and changelogs is a content change`` () =
    test
        <@
            bumpOnly fsprojs changelogs [ "src/Cli/CHANGELOG.md"; "src/Cli/Program.fs" ] fileAt "base111" "bump222" =
                Error
                    "src/Cli/Program.fs changed since the canary ran, and it is not a version or changelog the release bumps"
        @>

[<Fact>]
let ``bumpOnly - an fsproj that changed more than its Version is a content change`` () =
    test
        <@
            bumpOnly fsprojs changelogs [ "src/Core/Core.fsproj" ] fileAt "base111" "bump222" =
                Error "src/Core/Core.fsproj changed since the canary ran beyond its <Version>"
        @>

[<Fact>]
let ``bumpOnly - an fsproj that cannot be read on either side is a content change`` () =
    let missing (_: string) (_: string) : string option = None

    test
        <@
            bumpOnly fsprojs changelogs [ "src/Cli/Cli.fsproj" ] missing "base111" "bump222" =
                Error "src/Cli/Cli.fsproj changed since the canary ran beyond its <Version>"
        @>

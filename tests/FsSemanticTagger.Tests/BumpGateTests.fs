/// The version-bump commit skips its own full CI wait only when it changes nothing but the
/// released `<Version>` elements and changelogs (`bumpOnlySince`, which reads each fsproj at both
/// revisions), over a single parent whose full CI passed (AUTOMATION-1750). Anything else waits,
/// as before.
module FsSemanticTagger.Tests.BumpGateTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.Release

let private bump = String.replicate 40 "b"
let private parent = String.replicate 40 "a"

let private pkg =
    {
        Name = "A"
        Fsproj = "src/A/A.fsproj"
        DllPath = "src/A/bin/Release/net10.0/A.dll"
        TagPrefix = "a-v"
        FsProjsSharingSameTag = []
    }

let private config =
    {
        Packages =
            [
                pkg
                { pkg with
                    Name = "B"
                    Fsproj = "src/B/B.fsproj"
                    TagPrefix = "b-v"
                }
            ]
        ReservedVersions = Set.empty
        PreBuildCmds = []
        PublishWorkflows = defaultPublishWorkflows
        CiTimeout = None
        RootDir = "/repo"
    }

let private bumps = [ pkg, parse "1.0.1" ]

let private fsproj (version: string) (extra: string) =
    $"<Project>\n  <PropertyGroup>\n    <Version>%s{version}</Version>%s{extra}\n  </PropertyGroup>\n</Project>\n"

let private ciRuns (conclusion: string) =
    $"""[{{"databaseId":11,"attempt":1,"createdAt":"2026-11-16T12:00:00Z","workflowDatabaseId":77,"status":"completed","conclusion":"%s{conclusion}","name":"CI","url":"run"}}]"""

/// `jj` and `gh` answering as a repository whose `main` is a bump commit over `parent`, changing
/// `changed`, with `after` as A.fsproj in the bump.
let private repo (changed: string list) (after: string) (parentCi: string) : string -> string -> CommandResult =
    fun cmd args ->
        match cmd, args with
        | "jj", "log -r main --no-graph -T commit_id" -> Success bump
        | "jj", "log -r main- --no-graph -T commit_id" -> Success parent
        | "jj", a when a = $"diff --from %s{parent} --to %s{bump} --name-only" -> Success(String.concat "\n" changed)
        | "jj", a when a = $"file show -r \"%s{parent}\" \"src/A/A.fsproj\"" -> Success(fsproj "1.0.0" "")
        | "jj", a when a = $"file show -r \"%s{bump}\" \"src/A/A.fsproj\"" -> Success after
        | "gh", a when a.Contains $"--commit %s{parent}" -> Success parentCi
        | _ -> Failure($"unexpected: %s{cmd} %s{args}", 1)

let private versionOnly =
    repo [ "src/A/A.fsproj"; "src/A/CHANGELOG.md" ] (fsproj "1.0.1" "")

[<Fact>]
let ``a bump of versions and changelogs over a green parent need not wait for its own CI`` () =
    test <@ bumpOverGreenParent (versionOnly (ciRuns "success")) config bumps = Ok(bump, parent) @>

[<Fact>]
let ``a bump whose parent's CI did not pass waits`` () =
    test
        <@
            bumpOverGreenParent (versionOnly (ciRuns "failure")) config bumps
            |> Result.isError
        @>

    test <@ bumpOverGreenParent (versionOnly "[]") config bumps |> Result.isError @>

[<Fact>]
let ``a bump that changes an fsproj beyond its version waits`` () =
    let run =
        repo [ "src/A/A.fsproj" ] (fsproj "1.0.1" "\n    <Nullable>enable</Nullable>") (ciRuns "success")

    test <@ bumpOverGreenParent run config bumps |> Result.isError @>

[<Fact>]
let ``a bump that changes any other file waits`` () =
    let run =
        repo [ "src/A/A.fsproj"; "src/A/Lib.fs" ] (fsproj "1.0.1" "") (ciRuns "success")

    test <@ bumpOverGreenParent run config bumps |> Result.isError @>

[<Fact>]
let ``a bump of a package this release does not name waits`` () =
    let run = repo [ "src/B/B.fsproj" ] (fsproj "1.0.1" "") (ciRuns "success")
    test <@ bumpOverGreenParent run config bumps |> Result.isError @>

[<Fact>]
let ``a merge as the bump commit waits`` () =
    let run cmd args =
        match cmd, args with
        | "jj", "log -r main- --no-graph -T commit_id" -> Success(parent + parent)
        | _ -> versionOnly (ciRuns "success") cmd args

    test <@ bumpOverGreenParent run config bumps |> Result.isError @>

[<Fact>]
let ``an unreadable diff waits`` () =
    let run cmd (args: string) =
        if cmd = "jj" && args.StartsWith "diff" then
            Failure("jj: no such revision", 1)
        else
            versionOnly (ciRuns "success") cmd args

    test <@ bumpOverGreenParent run config bumps |> Result.isError @>

let private bumpCi (conclusion: string) : string -> string -> CommandResult =
    fun cmd args ->
        if cmd = "gh" && args.Contains $"--commit %s{bump}" then
            Success(ciRuns conclusion)
        else
            Failure($"unexpected: %s{cmd} %s{args}", 1)

[<Fact>]
let ``a red on the bump commit after a published release exits with its own code, not a failed release's`` () =
    test <@ reportBumpCi (bumpCi "failure") bump 0 = PublishedBumpCiRed @>
    test <@ PublishedBumpCiRed <> 1 && PublishedBumpCiRed <> 2 @>

[<Fact>]
let ``a release whose waves did not finish keeps their exit code whatever the bump commit's CI says`` () =
    test <@ reportBumpCi (bumpCi "failure") bump 1 = 1 @>
    test <@ reportBumpCi (bumpCi "failure") bump 2 = 2 @>

[<Fact>]
let ``a green or not yet finished bump commit leaves the release's exit code alone`` () =
    test <@ reportBumpCi (bumpCi "success") bump 0 = 0 @>
    test <@ reportBumpCi (bumpCi "cancelled") bump 0 = 0 @>

[<Fact>]
let ``each way the parent's CI falls short says which`` () =
    let running =
        """[{"databaseId":11,"attempt":1,"createdAt":"2026-11-16T12:00:00Z","workflowDatabaseId":77,"status":"in_progress","conclusion":null,"name":"CI","url":"run"}]"""

    let reasonFor (parentCi: CommandResult) =
        let run cmd (args: string) =
            if cmd = "gh" then
                parentCi
            else
                versionOnly (ciRuns "success") cmd args

        match bumpOverGreenParent run config bumps with
        | Error reason -> reason
        | Ok _ -> "passed"

    test <@ (reasonFor (Success(ciRuns "failure"))).EndsWith "failed CI" @>
    test <@ (reasonFor (Success running)).EndsWith "still running" @>
    test <@ (reasonFor (Success "[]")).EndsWith "has no CI run" @>
    test <@ (reasonFor (Failure("gh: not logged in", 1))).EndsWith "could not be read" @>

[<Fact>]
let ``a bump commit whose own CI is still running or unreadable leaves the release's exit code alone`` () =
    let running =
        """[{"databaseId":11,"attempt":1,"createdAt":"2026-11-16T12:00:00Z","workflowDatabaseId":77,"status":"in_progress","conclusion":null,"name":"CI","url":"run"}]"""

    test <@ reportBumpCi (fun _ _ -> Success running) bump 0 = 0 @>
    test <@ reportBumpCi (fun _ _ -> Failure("gh: not logged in", 1)) bump 0 = 0 @>

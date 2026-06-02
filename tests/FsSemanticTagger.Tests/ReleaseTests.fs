module FsSemanticTagger.Tests.ReleaseTests

open System.IO
open Xunit
open Tests.Common
open Tests.Common.TestHelpers
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.Release
open FsSemanticTagger.Api
open FsSemanticTagger.Vcs

let private noPreviousApi (_tag: string) (_dll: string) : ApiSignature list option = None
let private noCurrentApi (_dll: string) : ApiSignature list = []

/// Release tests put fsproj files in the system temp dir, so CHANGELOG.md
/// also lives there. Re-seeds before every release call (promotion mutates it).
let private seedTmpChangelog () =
    let p = Path.Combine(Path.GetTempPath(), "CHANGELOG.md")
    File.WriteAllText(p, "# Changelog\n\n## Unreleased\n\n- test entry\n")

let private runRelease run config cmd mode prev cur poll max =
    seedTmpChangelog ()

    release
        { Run = run
          Config =
            { config with
                RootDir = Path.GetTempPath() }
          Command = cmd
          Mode = mode
          TargetPackages = []
          ExtractPreviousApi = prev
          ExtractCurrentApi = cur
          CiPollIntervalMs = poll
          CiMaxAttempts = max
          CheckPublished = (fun _ _ -> true)
          WaitForNuGet = false
          NuGetPollIntervalMs = 0
          NuGetMaxAttempts = 1 }

[<Fact>]
let ``updateFsprojVersion - updates Version element in fsproj`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            { Major = 2
              Minor = 3
              Patch = 4
              Stage = Stable }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>2.3.4</Version>") @>
        test <@ not (result.Contains("<Version>1.0.0</Version>")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``updateFsprojVersion - handles pre-release versions`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let content =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, content)

        let newVersion =
            { Major = 0
              Minor = 2
              Patch = 0
              Stage = PreRelease(Alpha 1) }

        updateFsprojVersion tmpFile newVersion
        let result = File.ReadAllText(tmpFile)
        test <@ result.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``readFsprojVersion - reads version from fsproj`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>1.2.3-alpha.4</Version>
  </PropertyGroup>
</Project>"""
        )

        let result = readFsprojVersion tmpFile
        test <@ result = Some(parse "1.2.3-alpha.4") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``readFsprojVersion - returns None when no Version element`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>"""
        )

        let result = readFsprojVersion tmpFile
        test <@ result = None @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - returns 1 when uncommitted changes`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "Working copy changes:\nM src/Foo.fs"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - returns 1 when CI not passing`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - Auto with no previous tags returns 0 with no packages`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "git", arg when arg.StartsWith("tag -l") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - StartAlpha with FirstRelease tags and bumps version`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(
            tmpFile,
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>
</Project>"""
        )

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        // Verify tag was set on immutable commit
        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set v0.1.0-alpha.1"))
            @>

        // Verify fsproj was updated
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.1.0-alpha.1</Version>") @>

        // Verify commit + bookmark advance happened after tagging
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit")) @>

        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set main")) @>
    finally
        File.Delete(tmpFile)

/// Helper: standard fakeRun responses for a passing CI + clean working copy
let private passingCiRun (extraResponses: (string * string * CommandResult) list) =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        let extra = extraResponses |> List.tryFind (fun (c, a, _) -> c = cmd && a = args)

        match extra with
        | Some(_, _, r) -> r
        | None ->
            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | "dotnet", arg when arg.StartsWith("pack") -> Success "Successfully created package"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    (fakeRun, (fun () -> calls))

[<Fact>]
let ``release - StartAlpha with LocalPublish calls dotnet pack`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha LocalPublish noPreviousApi noCurrentApi 0 10

        let calls = getCalls ()
        test <@ result = 0 @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "dotnet" && a.StartsWith("pack") && a.Contains(tmpFile))
            @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto with reserved version bumps past it`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        // Auto with prior tag v1.0.0, unchanged API => patch bump => 1.0.1, but that's reserved => 1.0.2
        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0")
                  // hasChangesSinceTag: report changes
                  ("jj",
                   "diff --from v1.0.0 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        // Identical old/new API => NoChange => patch bump path (exercises reserved-version skip)
        let sameApi = [ ApiSignature "type Foo" ]
        let extractPreviousApi (_tag: string) (_dllPath: string) = Some sameApi

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.ofList [ "1.0.1" ]
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> sameApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.2</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - non-Auto with reserved version skips package`` () =
    let (fakeRun, _getCalls) = passingCiRun []

    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.ofList [ "0.1.0-alpha.1" ]
          PreBuildCmds = []
          RootDir = "" }

    let result =
        runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - PromoteToBeta with FirstRelease returns 0 no packages`` () =
    let (fakeRun, _getCalls) = passingCiRun []

    let config =
        { Packages =
            [ { Name = "MyLib"
                Fsproj = "src/MyLib/MyLib.fsproj"
                DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                TagPrefix = "v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result =
        runRelease fakeRun config PromoteToBeta PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

[<Fact>]
let ``release - runs preBuildCmds before build`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, getCalls) =
            passingCiRun
                [ ("dotnet", "tool restore", Success "Restored.")
                  ("dotnet", "tool run paket restore", Success "Paket restored.") ]

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = [ "dotnet tool restore"; "dotnet tool run paket restore" ]
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        let calls = getCalls ()
        test <@ result = 0 @>

        // Verify pre-build commands ran before build
        let toolRestoreIdx =
            calls |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "tool restore")

        let paketRestoreIdx =
            calls
            |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "tool run paket restore")

        let buildIdx =
            calls |> List.findIndex (fun (c, a) -> c = "dotnet" && a = "build -c Release")

        test <@ toolRestoreIdx < paketRestoreIdx @>
        test <@ paketRestoreIdx < buildIdx @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForCi - polls until CI passes`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            if ghCallCount <= 2 then
                Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
            else
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 10 // 0ms poll interval for tests
    test <@ result = Passed @>
    test <@ ghCallCount = 3 @>

[<Fact>]
let ``waitForCi - times out when CI stays in progress`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 3 // max 3 attempts

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

[<Fact>]
let ``waitForCi - returns Failed immediately without polling`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            Success """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 10

    test
        <@
            match result with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ ghCallCount = 1 @>

[<Fact>]
let ``release - skips packages with no changes since last tag`` () =
    let tmpFileA = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFileA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            // LibA has a previous tag and changes
            | "jj", a when a.Contains("tag list") && a.Contains("liba-v") -> Success "liba-v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from liba-v0.1.0-alpha.1") -> Success "1 file changed"
            // LibB has a previous tag but NO changes
            | "jj", a when a.Contains("tag list") && a.Contains("libb-v") -> Success "libb-v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from libb-v0.1.0-alpha.1") -> Success ""
            // tagging and push responses

            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "LibA"
                    Fsproj = tmpFileA
                    DllPath = "src/LibA/bin/Release/net10.0/LibA.dll"
                    TagPrefix = "liba-v"
                    FsProjsSharingSameTag = [] }
                  { Name = "LibB"
                    Fsproj = "src/LibB/LibB.fsproj"
                    DllPath = "src/LibB/bin/Release/net10.0/LibB.dll"
                    TagPrefix = "libb-v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        // LibA should be tagged
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set liba-v")) @>

        // LibB should NOT be tagged
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set libb-v"))) @>
    finally
        File.Delete(tmpFileA)

[<Fact>]
let ``release - Auto detects breaking API change and bumps major`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0")
                  ("jj",
                   "diff --from v1.0.0 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        let oldApi = [ ApiSignature "type Foo"; ApiSignature "  Foo::Bar(): String" ]

        // Current API removed Bar (breaking)
        let currentApi = [ ApiSignature "type Foo" ]

        let extractPreviousApi (_tag: string) (_dllPath: string) = Some oldApi

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Breaking change on v1+ => major bump => 2.0.0
        test <@ content.Contains("<Version>2.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto detects addition and bumps minor`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0")
                  ("jj",
                   "diff --from v1.0.0 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        let oldApi = [ ApiSignature "type Foo" ]

        // Current API added a method (addition)
        let currentApi =
            [ ApiSignature "type Foo"; ApiSignature "  Foo::NewMethod(): String" ]

        let extractPreviousApi (_tag: string) (_dllPath: string) = Some oldApi

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Addition on v1+ => minor bump => 1.1.0
        test <@ content.Contains("<Version>1.1.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto aborts (no bump) when previous API cannot be read`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0")
                  ("jj",
                   "diff --from v1.0.0 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        // Previous API unavailable (e.g. package not cached and download failed).
        let extractPreviousApi (_tag: string) (_dllPath: string) = None

        let currentApi =
            [ ApiSignature "type Foo"; ApiSignature "  Foo::NewMethod(): String" ]

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        // Must refuse to guess — exit non-zero and leave the fsproj version untouched.
        // Silently bumping patch here is the bug that shipped a breaking change as 0.3.1.
        test <@ result = 1 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - Auto pre-1.0 breaking change bumps minor (UnionConfig 0.3.0 -> 0.4.0)`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.3.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v0.3.0")
                  ("jj",
                   "diff --from v0.3.0 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        // Mirrors the real change: a DU case lost its payload (breaking removal).
        let oldApi =
            [ ApiSignature "type ConfigVarKind"
              ApiSignature "  ConfigVarKind+AutoGenerated"
              ApiSignature "  AutoGenerated::initialValue: FSharpOption<String>" ]

        let currentApi =
            [ ApiSignature "type ConfigVarKind"
              ApiSignature "  ConfigVarKind+AutoGenerated" ]

        let extractPreviousApi (_tag: string) (_dllPath: string) = Some oldApi

        let config =
            { Packages =
                [ { Name = "UnionConfig"
                    Fsproj = tmpFile
                    DllPath = "fake.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config Auto PushTags extractPreviousApi (fun _ -> currentApi) 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        // Pre-1.0 breaking change => minor bump => 0.4.0 (not patch 0.3.1)
        test <@ content.Contains("<Version>0.4.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI fails`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    // First CI check (pre-release): passing
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    // Second CI check (post-push): failed
                    Success
                        """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>

        // Should NOT have pushed tags
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git export")) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI times out`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 3

        test <@ result = 1 @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - does not push tags when post-push CI has no runs`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    // Post-push: no runs found
                    Success "[]"
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", a when a.StartsWith("commit") -> Success ""
            | "jj", a when a.StartsWith("bookmark set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "log -r @- --no-graph -T commit_id" -> Success "def456"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>

        // Should NOT have pushed tags
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - uses coverageratchet loosen-from-ci when available`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
        | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") -> Success ""
        | "dotnet", "build -c Release" -> Success "Build succeeded."
        | "git", arg when arg.StartsWith("tag -l") -> Success ""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 0 @>

    // Should have called coverageratchet instead of gh run list
    test
        <@
            calls
            |> List.exists (fun (c, a) -> c = "dotnet" && a.Contains("coverageratchet loosen-from-ci"))
        @>

    test <@ not (calls |> List.exists (fun (c, _) -> c = "gh")) @>

[<Fact>]
let ``release - returns 1 when coverageratchet loosen-from-ci fails`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
        | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") -> Failure "CI not green"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - prints coverageratchet error message when loosen-from-ci fails`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "dotnet", "tool list" -> Success "coverageratchet    0.8.0-alpha.4    coverageratchet"
        | "dotnet", a when a.StartsWith("tool run coverageratchet loosen-from-ci") ->
            Failure "CI failed for non-coverage reasons."
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let output, result =
        withCapturedConsole (fun () -> runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10)

    test <@ result = 1 @>
    test <@ output.Contains("CI failed for non-coverage reasons.") @>

[<Fact>]
let ``release - returns 1 when CI has no runs`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") -> Success "[]"
        | "jj", "log -r @- --no-graph -T commit_id" -> Success "def456"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - returns 1 when CI status is Unknown`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") -> Failure "gh not installed"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 10

    test <@ result = 1 @>

[<Fact>]
let ``release - returns 1 when CI times out still in progress`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "The working copy is clean"
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let result = runRelease fakeRun config Auto PushTags noPreviousApi noCurrentApi 0 2

    test <@ result = 1 @>

[<Fact>]
let ``release - PromoteToRC with HasPreviousRelease succeeds`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0-beta.3</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0-beta.3")
                  ("jj",
                   "diff --from v1.0.0-beta.3 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config PromoteToRC PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0-rc.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - PromoteToStable with HasPreviousRelease succeeds`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>1.0.0-rc.1</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v1.0.0-rc.1")
                  ("jj",
                   "diff --from v1.0.0-rc.1 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config PromoteToStable PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - PromoteToBeta with HasPreviousRelease succeeds`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.1.0-alpha.3</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) =
            passingCiRun
                [ ("git", "tag -l \"v*\"", Success "v0.1.0-alpha.3")
                  ("jj",
                   "diff --from v0.1.0-alpha.3 --to @ --summary \"glob:"
                   + Path.GetDirectoryName(tmpFile)
                   + "/**\"",
                   Success "1 file changed") ]

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config PromoteToBeta PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.1.0-beta.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForCi - returns Passed immediately when CI passes`` () =
    let mutable ghCallCount = 0

    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") ->
            ghCallCount <- ghCallCount + 1

            Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 10
    test <@ result = Passed @>
    test <@ ghCallCount = 1 @>

[<Fact>]
let ``waitForCi - returns NoRuns immediately`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") -> Success "[]"
        | "jj", "status" -> Success "Working copy changes:\nM src/Foo.fs"
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 10
    test <@ result = NoRuns @>

[<Fact>]
let ``waitForCi - returns Unknown immediately`` () =
    let run (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
        | "gh", a when a.Contains("run list") -> Failure "gh not found"
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    let result = waitForCi run 0 10
    test <@ result = Unknown @>

[<Fact>]
let ``release - updates fsProjsSharingSameTag versions too`` () =
    let tmpFileMain = Path.GetTempFileName()
    let tmpFileShared = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFileMain, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        File.WriteAllText(tmpFileShared, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")

        let (fakeRun, _getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFileMain
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [ tmpFileShared ] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        let mainContent = File.ReadAllText(tmpFileMain)
        let sharedContent = File.ReadAllText(tmpFileShared)
        test <@ mainContent.Contains("<Version>0.1.0-alpha.1</Version>") @>
        test <@ sharedContent.Contains("<Version>0.1.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFileMain)
        File.Delete(tmpFileShared)

[<Fact>]
let ``release - resumes when fsproj already has target version (idempotent)`` () =
    let tmpFile = Path.GetTempFileName()

    try
        // Simulate: previous run already bumped to 0.2.0-alpha.1
        // (StartAlpha with prev tag v0.1.0-alpha.1 => nextAlphaCycle => 0.2.0-alpha.1)
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            // Latest tag is the previous version (alpha.1), and there are changes
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            // tagExists check for the target version tag — not yet pushed
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            // Tag operations for resumed path
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git push" -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>

        // Should NOT have committed (no version change needed)
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>

        // Should NOT have set bookmark
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set"))) @>

        // SHOULD have pushed tags
        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin")) @>

        // fsproj should still have the same version (not double-bumped)
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - fails fast when resuming and CI has failed`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable ghCallCount = 0

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                else
                    Success
                        """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/2"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 1 @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - resumes and polls when CI is in progress`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable ghCallCount = 0
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                ghCallCount <- ghCallCount + 1

                if ghCallCount <= 1 then
                    // Pre-release CI check: passing
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
                elif ghCallCount <= 3 then
                    // Resumed path polls: in progress
                    Success """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""
                else
                    // Eventually passes
                    Success
                        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // Polled multiple times (1 pre-release + 2 in-progress + 1 success = 4)
        test <@ ghCallCount = 4 @>
        // Tags were pushed
        test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - second run after successful first run produces no changes`` () =
    let tmpFile = Path.GetTempFileName()

    try
        // After successful release: fsproj has new version AND tag exists at that version
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            // Tag exists at the NEW version -- means first run fully succeeded
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.2.0-alpha.1"
            // No changes since tag (first run already committed everything)
            | "jj", a when a.Contains("--from v0.2.0-alpha.1") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha PushTags noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // Version not changed
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - aborts with exit 1 when CHANGELOG has no Unreleased section`` () =
    let tmpFile = Path.GetTempFileName()
    let changelogPath = Path.Combine(Path.GetTempPath(), "CHANGELOG.md")

    try
        let fsprojBefore =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><Version>0.0.0</Version></PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojBefore)
        // CHANGELOG with no Unreleased section -> validation must fail before any writes
        File.WriteAllText(changelogPath, "# Changelog\n\n## 0.1.0 - 2026-01-01\n\n- stuff\n")

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = Path.GetTempPath() }

        let result =
            release
                { Run = fakeRun
                  Config = config
                  Command = StartAlpha
                  Mode = PushTags
                  TargetPackages = []
                  ExtractPreviousApi = noPreviousApi
                  ExtractCurrentApi = noCurrentApi
                  CiPollIntervalMs = 0
                  CiMaxAttempts = 10
                  CheckPublished = (fun _ _ -> true)
                  WaitForNuGet = false
                  NuGetPollIntervalMs = 0
                  NuGetMaxAttempts = 1 }

        test <@ result = 1 @>
        // fsproj untouched
        test <@ File.ReadAllText(tmpFile) = fsprojBefore @>
    finally
        File.Delete(tmpFile)

        if File.Exists changelogPath then
            File.Delete changelogPath

[<Fact>]
let ``release - dryRun skips uncommitted check and does not write fsproj`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojBefore =
            """<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>"""

        File.WriteAllText(tmpFile, fsprojBefore)

        let mutable calls = []

        // jj status reports uncommitted changes; in a normal release this aborts.
        // In dry-run the check should be skipped entirely. Also no CI calls, no commit/tag/push.
        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha DryRun noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // fsproj untouched
        test <@ File.ReadAllText(tmpFile) = fsprojBefore @>
        // no commit, tag, or push calls
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("commit"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>
        // no CI check
        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("run list"))) @>
        // explicit-mode dry-run skips the Release build
        test <@ not (calls |> List.exists (fun (c, a) -> c = "dotnet" && a = "build -c Release")) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - dryRun with missing Unreleased warns but still returns 0`` () =
    let tmpFile = Path.GetTempFileName()
    let tmpDir = createTempDir ()

    try
        let fsprojPath = Path.Combine(tmpDir, "MyLib.fsproj")
        File.WriteAllText(fsprojPath, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        // No CHANGELOG at repo root => validation would fail in real run

        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = fsprojPath
                    DllPath = "x.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = tmpDir }

        // Bypass the seedTmpChangelog helper; call release directly with rootDir = tmpDir (no CHANGELOG.md there)
        let output, result =
            withCapturedConsole (fun () ->
                release
                    { Run = fakeRun
                      Config = config
                      Command = StartAlpha
                      Mode = DryRun
                      TargetPackages = []
                      ExtractPreviousApi = noPreviousApi
                      ExtractCurrentApi = noCurrentApi
                      CiPollIntervalMs = 0
                      CiMaxAttempts = 10
                      CheckPublished = (fun _ _ -> true)
                      WaitForNuGet = false
                      NuGetPollIntervalMs = 0
                      NuGetMaxAttempts = 1 })

        test <@ result = 0 @>

        test
            <@
                output.ToLowerInvariant().Contains("warning")
                || output.ToLowerInvariant().Contains("changelog")
            @>
        // fsproj version not advanced
        test <@ File.ReadAllText(fsprojPath).Contains("<Version>0.0.0</Version>") @>
    finally
        File.Delete(tmpFile)
        cleanupDir tmpDir

[<Fact>]
let ``release - resume in DryRun mode takes no actions and returns 0`` () =
    let tmpFile = Path.GetTempFileName()

    try
        // fsproj already at the target version — AlreadyBumped path, DryRun short-circuit.
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha DryRun noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // DryRun resume must NOT set tags.
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a.StartsWith("tag set"))) @>
        // DryRun resume must NOT push.
        test <@ not (calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push")) @>

        // fsproj version unchanged.
        test <@ File.ReadAllText(tmpFile).Contains("<Version>0.2.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - resume with LocalPublish packs without pushing`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.2.0-alpha.1</Version></PropertyGroup></Project>")

        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "jj", a when a.Contains("tag list") && a.Contains("\"glob:v") -> Success "v0.1.0-alpha.1"
            | "jj", a when a.Contains("--from v0.1.0-alpha.1") -> Success "1 file changed"
            // tagExists probes (tag not yet set)
            | "jj", "tag list v0.2.0-alpha.1" -> Success ""
            | "git", "tag -l v0.2.0-alpha.1" -> Success ""
            | "jj", a when a.StartsWith("tag set") -> Success ""
            | "dotnet", arg when arg.StartsWith("pack") -> Success "Successfully created package"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runRelease fakeRun config StartAlpha LocalPublish noPreviousApi noCurrentApi 0 10

        test <@ result = 0 @>
        // LocalPublish resume must pack.
        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "dotnet" && a.StartsWith("pack") && a.Contains(tmpFile))
            @>
        // LocalPublish resume must NOT push tags.
        test <@ not (calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("push origin"))) @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``waitForNuGet - returns true when all packages already published`` () =
    let mutable checks = 0

    let checkPublished (_id: string) (_ver: string) =
        checks <- checks + 1
        true

    let result = waitForNuGet checkPublished 0 5 [ "PkgA", "1.0.0"; "PkgB", "2.0.0" ]

    test <@ result = true @>
    // One check per package, no polling rounds beyond the first.
    test <@ checks = 2 @>

[<Fact>]
let ``waitForNuGet - polls until a package becomes available`` () =
    let mutable attempts = 0

    // Not published for the first two checks, then available.
    let checkPublished (_id: string) (_ver: string) =
        attempts <- attempts + 1
        attempts >= 3

    let result = waitForNuGet checkPublished 0 10 [ "PkgA", "1.0.0" ]

    test <@ result = true @>
    test <@ attempts >= 3 @>

[<Fact>]
let ``waitForNuGet - returns false when never published (times out)`` () =
    let checkPublished (_id: string) (_ver: string) = false
    let result = waitForNuGet checkPublished 0 3 [ "PkgA", "1.0.0" ]
    test <@ result = false @>

[<Fact>]
let ``waitForNuGet - maxAttempts 1 does exactly one check then times out`` () =
    let mutable checks = 0

    let checkPublished (_id: string) (_ver: string) =
        checks <- checks + 1
        false

    let result = waitForNuGet checkPublished 0 1 [ "PkgA", "1.0.0" ]
    test <@ result = false @>
    test <@ checks = 1 @>

/// Like runRelease but lets the caller drive the NuGet-availability wait.
let private runReleaseWithNuGetWait run config cmd checkPublished maxAttempts =
    seedTmpChangelog ()

    release
        { Run = run
          Config =
            { config with
                RootDir = Path.GetTempPath() }
          Command = cmd
          Mode = PushTags
          TargetPackages = []
          ExtractPreviousApi = noPreviousApi
          ExtractCurrentApi = noCurrentApi
          CiPollIntervalMs = 0
          CiMaxAttempts = 10
          CheckPublished = checkPublished
          WaitForNuGet = true
          NuGetPollIntervalMs = 0
          NuGetMaxAttempts = maxAttempts }

[<Fact>]
let ``release - waits for NuGet after pushing tags and checks the published package`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _getCalls) = passingCiRun []

        let mutable checked' = []

        let checkPublished (id: string) (ver: string) =
            checked' <- checked' @ [ (id, ver) ]
            true

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result = runReleaseWithNuGetWait fakeRun config StartAlpha checkPublished 5

        test <@ result = 0 @>
        // The just-released package id + version were polled on NuGet.
        test <@ checked' |> List.contains ("MyLib", "0.1.0-alpha.1") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - NuGet wait timeout does not change the exit code`` () =
    let tmpFile = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFile, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, _getCalls) = passingCiRun []

        // Never published -> waitForNuGet times out, but release already pushed tags.
        let checkPublished (_id: string) (_ver: string) = false

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result = runReleaseWithNuGetWait fakeRun config StartAlpha checkPublished 2

        // Timeout is a convenience-wait failure; the release succeeded.
        test <@ result = 0 @>
    finally
        File.Delete(tmpFile)

// ---------------------------------------------------------------------------
// --only / selectPackages: scope a run to specific packages by name
// ---------------------------------------------------------------------------

let private pkg name fsproj prefix : PackageConfig =
    { Name = name
      Fsproj = fsproj
      DllPath = sprintf "%s.dll" name
      TagPrefix = prefix
      FsProjsSharingSameTag = [] }

[<Fact>]
let ``selectPackages - empty target returns all packages unchanged`` () =
    let pkgs = [ pkg "A" "a.fsproj" "a-v"; pkg "B" "b.fsproj" "b-v" ]
    test <@ selectPackages [] pkgs = Ok pkgs @>

[<Fact>]
let ``selectPackages - single name returns only that package`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"
    test <@ selectPackages [ "B" ] [ a; b ] = Ok [ b ] @>

[<Fact>]
let ``selectPackages - multiple names return those packages preserving order`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"
    let c = pkg "C" "c.fsproj" "c-v"
    test <@ selectPackages [ "A"; "C" ] [ a; b; c ] = Ok [ a; c ] @>

[<Fact>]
let ``selectPackages - unknown name errors listing valid names`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"

    match selectPackages [ "Nope" ] [ a; b ] with
    | Error msg ->
        test <@ msg.Contains("Nope") @>
        test <@ msg.Contains("A") && msg.Contains("B") @>
    | Ok _ -> failwith "expected an error for an unknown package name"

[<Fact>]
let ``selectPackages - one unknown among known names still errors`` () =
    let a = pkg "A" "a.fsproj" "a-v"
    let b = pkg "B" "b.fsproj" "b-v"

    match selectPackages [ "A"; "Nope" ] [ a; b ] with
    | Error msg -> test <@ msg.Contains("Nope") @>
    | Ok _ -> failwith "expected an error when any name is unknown"

/// Like runRelease, but threads a `--only`-style target-package list.
let private runReleaseTargeting run config cmd mode targets =
    seedTmpChangelog ()

    release
        { Run = run
          Config =
            { config with
                RootDir = Path.GetTempPath() }
          Command = cmd
          Mode = mode
          TargetPackages = targets
          ExtractPreviousApi = noPreviousApi
          ExtractCurrentApi = noCurrentApi
          CiPollIntervalMs = 0
          CiMaxAttempts = 10
          CheckPublished = (fun _ _ -> true)
          WaitForNuGet = false
          NuGetPollIntervalMs = 0
          NuGetMaxAttempts = 1 }

[<Fact>]
let ``release - scoped to one package only tags that package`` () =
    let tmpFileA = Path.GetTempFileName()

    try
        File.WriteAllText(tmpFileA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "LibA"
                    Fsproj = tmpFileA
                    DllPath = "src/LibA/bin/Release/net10.0/LibA.dll"
                    TagPrefix = "liba-v"
                    FsProjsSharingSameTag = [] }
                  // LibB's fsproj does not exist on disk; if it were processed the
                  // run would crash reading its version, so this also proves LibB
                  // is fully out of scope.
                  { Name = "LibB"
                    Fsproj = "/no/such/LibB.fsproj"
                    DllPath = "src/LibB/bin/Release/net10.0/LibB.dll"
                    TagPrefix = "libb-v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result = runReleaseTargeting fakeRun config StartAlpha PushTags [ "LibA" ]

        let calls = getCalls ()
        test <@ result = 0 @>
        // LibA tagged
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set liba-v")) @>
        // LibB never tagged or touched
        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("libb-v"))) @>
    finally
        File.Delete(tmpFileA)

[<Fact>]
let ``release - scoped to multiple packages tags exactly those`` () =
    let tmpA = Path.GetTempFileName()
    let tmpC = Path.GetTempFileName()

    try
        File.WriteAllText(tmpA, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        File.WriteAllText(tmpC, "<Project><PropertyGroup><Version>0.0.0</Version></PropertyGroup></Project>")
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "LibA"
                    Fsproj = tmpA
                    DllPath = "a.dll"
                    TagPrefix = "liba-v"
                    FsProjsSharingSameTag = [] }
                  { Name = "LibB"
                    Fsproj = "/no/such/LibB.fsproj"
                    DllPath = "b.dll"
                    TagPrefix = "libb-v"
                    FsProjsSharingSameTag = [] }
                  { Name = "LibC"
                    Fsproj = tmpC
                    DllPath = "c.dll"
                    TagPrefix = "libc-v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let result =
            runReleaseTargeting fakeRun config StartAlpha PushTags [ "LibA"; "LibC" ]

        let calls = getCalls ()
        test <@ result = 0 @>
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set liba-v")) @>
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set libc-v")) @>
        test <@ not (calls |> List.exists (fun (_, a) -> a.Contains("libb-v"))) @>
    finally
        File.Delete(tmpA)
        File.Delete(tmpC)

[<Fact>]
let ``release - unknown target package aborts with exit 1 before any work`` () =
    let mutable calls = []

    let fakeRun (cmd: string) (args: string) : CommandResult =
        calls <- calls @ [ (cmd, args) ]
        Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages =
            [ { Name = "LibA"
                Fsproj = "a.fsproj"
                DllPath = "a.dll"
                TagPrefix = "liba-v"
                FsProjsSharingSameTag = [] } ]
          ReservedVersions = Set.empty
          PreBuildCmds = []
          RootDir = "" }

    let output, result =
        withCapturedConsole (fun () -> runReleaseTargeting fakeRun config StartAlpha PushTags [ "Nope" ])

    test <@ result = 1 @>
    // Lists the valid name and the bad one; never ran the working-copy/CI checks.
    test <@ output.Contains("Nope") && output.Contains("LibA") @>
    test <@ List.isEmpty calls @>

[<Fact>]
let ``release - scoping composes with dry-run (only target previewed)`` () =
    let tmpA = Path.GetTempFileName()

    try
        File.WriteAllText(tmpA, "<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>")

        // Dry-run StartAlpha skips build/CI; only a tag-list lookup happens.
        let fakeRun (cmd: string) (args: string) : CommandResult =
            match cmd, args with
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", a when a.Contains("tag list") -> Success ""
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

        let config =
            { Packages =
                [ { Name = "LibA"
                    Fsproj = tmpA
                    DllPath = "a.dll"
                    TagPrefix = "liba-v"
                    FsProjsSharingSameTag = [] }
                  { Name = "LibB"
                    Fsproj = "/no/such/LibB.fsproj"
                    DllPath = "b.dll"
                    TagPrefix = "libb-v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = []
              RootDir = "" }

        let output, result =
            withCapturedConsole (fun () -> runReleaseTargeting fakeRun config StartAlpha DryRun [ "LibA" ])

        test <@ result = 0 @>
        // Targeting line names only LibA; LibB is out of scope (not even mentioned).
        test <@ output.Contains("Targeting: LibA") @>
        test <@ not (output.Contains("LibB")) @>
        // fsproj untouched in dry-run
        test <@ File.ReadAllText(tmpA).Contains("<Version>1.0.0</Version>") @>
    finally
        File.Delete(tmpA)

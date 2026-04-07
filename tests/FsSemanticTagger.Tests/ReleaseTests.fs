module FsSemanticTagger.Tests.ReleaseTests

open System.IO
open Xunit
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Config
open FsSemanticTagger.Version
open FsSemanticTagger.Release
open FsSemanticTagger.Vcs

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
let ``release - returns 1 when uncommitted changes`` () =
    let fakeRun (cmd: string) (args: string) : CommandResult =
        match cmd, args with
        | "jj", "status" -> Success "Working copy changes:\nM src/Foo.fs"
        | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    let config =
        { Packages = []
          ReservedVersions = Set.empty
          PreBuildCmds = [] }

    let result = release fakeRun config Auto GitHubActions
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
          PreBuildCmds = [] }

    let result = release fakeRun config Auto GitHubActions
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
          PreBuildCmds = [] }

    let result = release fakeRun config Auto GitHubActions
    test <@ result = 0 @>

[<Fact>]
let ``release - StartAlpha with FirstRelease creates version`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)

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
            | "jj", arg when arg.StartsWith("commit -m") -> Success ""
            | "jj", arg when arg.StartsWith("tag set") -> Success ""
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
              PreBuildCmds = [] }

        let result = release fakeRun config StartAlpha GitHubActions
        test <@ result = 0 @>

        // Verify the fsproj was updated with firstAlpha version
        let updatedContent = File.ReadAllText(tmpFile)
        test <@ updatedContent.Contains("<Version>0.1.0-alpha.1</Version>") @>

        // Verify commit and tag were called
        test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("commit")) @>

        test
            <@
                calls
                |> List.exists (fun (c, a) -> c = "jj" && a.Contains("tag set v0.1.0-alpha.1"))
            @>
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
            | "jj", arg when arg.StartsWith("commit -m") -> Success ""
            | "jj", arg when arg.StartsWith("tag set") -> Success ""
            | "jj", "git export" -> Success ""
            | "git", arg when arg.StartsWith("push origin") -> Success ""
            | "dotnet", arg when arg.StartsWith("pack") -> Success "Successfully created package"
            | _ -> Failure(sprintf "unexpected call: %s %s" cmd args)

    (fakeRun, (fun () -> calls))

[<Fact>]
let ``release - StartAlpha with LocalPublish calls dotnet pack`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)
        let (fakeRun, getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = [] }

        let result = release fakeRun config StartAlpha LocalPublish
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
let ``release - FsProjsSharingSameTag updates both fsproj files`` () =
    let tmpFile1 = Path.GetTempFileName()
    let tmpFile2 = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile1, fsprojContent)
        File.WriteAllText(tmpFile2, fsprojContent)
        let (fakeRun, _getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile1
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [ tmpFile2 ] } ]
              ReservedVersions = Set.empty
              PreBuildCmds = [] }

        let result = release fakeRun config StartAlpha GitHubActions
        test <@ result = 0 @>
        let content1 = File.ReadAllText(tmpFile1)
        let content2 = File.ReadAllText(tmpFile2)
        test <@ content1.Contains("<Version>0.1.0-alpha.1</Version>") @>
        test <@ content2.Contains("<Version>0.1.0-alpha.1</Version>") @>
    finally
        File.Delete(tmpFile1)
        File.Delete(tmpFile2)

[<Fact>]
let ``release - Auto with reserved version bumps past it`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>1.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)

        // Auto with prior tag v1.0.0 => patch bump => 1.0.1, but that's reserved => 1.0.2
        let (fakeRun, _getCalls) =
            passingCiRun [ ("git", "tag -l \"v*\"", Success "v1.0.0") ]

        // Use the test assembly itself as a real DLL so extractFromAssembly works
        let realDll = System.Reflection.Assembly.GetExecutingAssembly().Location

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = realDll
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.ofList [ "1.0.1" ]
              PreBuildCmds = [] }

        let result = release fakeRun config Auto GitHubActions
        test <@ result = 0 @>
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>1.0.2</Version>") @>
    finally
        File.Delete(tmpFile)

[<Fact>]
let ``release - non-Auto with reserved version skips package`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)
        let (fakeRun, _getCalls) = passingCiRun []

        let config =
            { Packages =
                [ { Name = "MyLib"
                    Fsproj = tmpFile
                    DllPath = "src/MyLib/bin/Release/net10.0/MyLib.dll"
                    TagPrefix = "v"
                    FsProjsSharingSameTag = [] } ]
              ReservedVersions = Set.ofList [ "0.1.0-alpha.1" ]
              PreBuildCmds = [] }

        let result = release fakeRun config StartAlpha GitHubActions
        test <@ result = 0 @>
        // Version should NOT have been updated since the package was skipped
        let content = File.ReadAllText(tmpFile)
        test <@ content.Contains("<Version>0.0.0</Version>") @>
    finally
        File.Delete(tmpFile)

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
          PreBuildCmds = [] }

    let result = release fakeRun config PromoteToBeta GitHubActions
    test <@ result = 0 @>

[<Fact>]
let ``release - runs preBuildCmds before build`` () =
    let tmpFile = Path.GetTempFileName()

    try
        let fsprojContent =
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.0.0</Version>
    <PackageId>MyLib</PackageId>
  </PropertyGroup>
</Project>"""

        File.WriteAllText(tmpFile, fsprojContent)
        let mutable calls = []

        let fakeRun (cmd: string) (args: string) : CommandResult =
            calls <- calls @ [ (cmd, args) ]

            match cmd, args with
            | "jj", "status" -> Success "The working copy is clean"
            | "jj", "log -r @ --no-graph -T commit_id" -> Success "abc123"
            | "gh", a when a.Contains("run list") ->
                Success """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""
            | "dotnet", "tool restore" -> Success "Restored."
            | "dotnet", "tool run paket restore" -> Success "Paket restored."
            | "dotnet", "build -c Release" -> Success "Build succeeded."
            | "git", arg when arg.StartsWith("tag -l") -> Success ""
            | "jj", arg when arg.StartsWith("commit -m") -> Success ""
            | "jj", arg when arg.StartsWith("tag set") -> Success ""
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
              PreBuildCmds = [ "dotnet tool restore"; "dotnet tool run paket restore" ] }

        let result = release fakeRun config StartAlpha GitHubActions
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

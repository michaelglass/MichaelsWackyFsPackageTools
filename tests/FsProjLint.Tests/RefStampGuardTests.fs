module FsProjLint.Tests.RefStampGuardTests

// The RefStamp local-pack guard: a repo that publishes
// packages must wire in RefStamp so a local `dotnet pack` cannot produce a
// release-shaped version. fsprojlint is the distribution's enforcement arm —
// the sibling repos adopt the guard because this check tells them to, with the
// exact line to add. The check is repo-level: ONE PackageReference in a root
// Directory.Build.props/targets covers every project, so adoption is one line
// per repo, not per fsproj (per-fsproj references are also accepted, as is a
// direct Import of RefStamp.targets — the shape the monorepo that OWNS
// RefStamp uses to dogfood it before the package exists on NuGet).

open Xunit
open Tests.Common
open Swensen.Unquote
open FsProjLint.Checks
open Tests.Common.TestHelpers
open FsProjLint.Tests.TestFixtures
open FsProjLint.Tests.GitFixtures

let private isPassed (result: CheckResult) = CheckOutcome.isPassed result.Outcome

let private isFailed (result: CheckResult) = CheckOutcome.isFailed result.Outcome

let private parse (xml: string) = projectOf xml

let private packableWithRefStamp =
    parse
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RefStamp" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>"""

let private packableWithoutRefStamp =
    parse
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>MyPackage</PackageId>
  </PropertyGroup>
</Project>"""

[<Fact>]
let ``passes when a root Directory Build props references RefStamp`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.props"
            """<Project>
  <ItemGroup>
    <PackageReference Include="RefStamp" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>"""

        let result = checkRefStampGuard dir [ packableWithoutRefStamp ]

        test <@ isPassed result @>)

[<Fact>]
let ``passes when a root Directory Build targets imports RefStamp targets`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "Directory.Build.targets"
            """<Project>
  <Import Project="src/RefStamp/build/RefStamp.targets" />
</Project>"""

        let result = checkRefStampGuard dir [ packableWithoutRefStamp ]

        test <@ isPassed result @>)

[<Fact>]
let ``passes when every packable fsproj references RefStamp`` () =
    withTempDir (fun dir ->
        let result = checkRefStampGuard dir [ packableWithRefStamp; packableWithRefStamp ]

        test <@ isPassed result @>)

[<Fact>]
let ``fails when only some packable fsprojs reference RefStamp`` () =
    withTempDir (fun dir ->
        let result =
            checkRefStampGuard dir [ packableWithRefStamp; packableWithoutRefStamp ]

        test <@ isFailed result @>)

[<Fact>]
let ``fails with a fix-me message naming Directory Build props`` () =
    withTempDir (fun dir ->
        let result = checkRefStampGuard dir [ packableWithoutRefStamp ]

        test <@ isFailed result @>

        match result.Outcome with
        | Failed reason ->
            test <@ reason.Contains "RefStamp" @>
            test <@ reason.Contains "Directory.Build.props" @>
        | Passed
        | Skipped _ -> failwith "expected the check to fail")

[<Fact>]
let ``an unparseable root Directory Build props does not count as a guard`` () =
    withTempDir (fun dir ->
        writeFile dir "Directory.Build.props" "<Project><not-closed</Project>"

        let result = checkRefStampGuard dir [ packableWithoutRefStamp ]

        test <@ isFailed result @>)

[<Fact>]
let ``passes when each project's nearest Directory Build props references RefStamp`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "src/Directory.Build.props"
            """<Project>
  <ItemGroup>
    <PackageReference Include="RefStamp" Version="0.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>"""

        writeFile
            dir
            "src/Lib/Lib.fsproj"
            "<Project><PropertyGroup><PackageId>Lib</PackageId></PropertyGroup></Project>"

        match Shared.MsBuildProject.load dir (System.IO.Path.Combine(dir, "src", "Lib", "Lib.fsproj")) with
        | Ok project -> test <@ isPassed (checkRefStampGuard dir [ project ]) @>
        | Error e -> failwith e)

// --- runLint wiring -----------------------------------------------------------

[<Fact>]
let ``runLint includes the guard check for packable repos`` () =
    withTempDir (fun dir ->
        writeFile dir "src/MyProject/MyProject.fsproj" packableFsproj

        let result = runLint dir

        test <@ result.RepoChecks |> List.exists (fun c -> c.Name.Contains "RefStamp") @>)

[<Fact>]
let ``runLint omits the guard check when nothing is packable`` () =
    withTempDir (fun dir ->
        writeFile
            dir
            "src/MyProject/MyProject.fsproj"
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>"""

        let result = runLint dir

        test <@ result.RepoChecks |> List.forall (fun c -> not (c.Name.Contains "RefStamp")) @>)

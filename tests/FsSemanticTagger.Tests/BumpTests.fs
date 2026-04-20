module FsSemanticTagger.Tests.BumpTests

open Xunit
open Tests.Common
open Swensen.Unquote
open FsSemanticTagger.Version
open FsSemanticTagger.Api
open FsSemanticTagger.Release

// determineBump: Stable >=1.0

[<Fact>]
let ``stable >=1.0 + Breaking bumps major`` () =
    let v =
        { Major = 1
          Minor = 2
          Patch = 3
          Stage = Stable }

    test <@ determineBump v (Breaking(ApiSignature "removed", [])) = bumpMajor v @>

[<Fact>]
let ``stable >=1.0 + Addition bumps minor`` () =
    let v =
        { Major = 1
          Minor = 2
          Patch = 3
          Stage = Stable }

    test <@ determineBump v (Addition(ApiSignature "added", [])) = bumpMinor v @>

[<Fact>]
let ``stable >=1.0 + NoChange bumps patch`` () =
    let v =
        { Major = 1
          Minor = 2
          Patch = 3
          Stage = Stable }

    test <@ determineBump v NoChange = bumpPatch v @>

// determineBump: Pre-1.0 stable

[<Fact>]
let ``pre-1.0 stable + Breaking bumps minor`` () =
    let v =
        { Major = 0
          Minor = 2
          Patch = 3
          Stage = Stable }

    test <@ determineBump v (Breaking(ApiSignature "removed", [])) = bumpMinor v @>

[<Fact>]
let ``pre-1.0 stable + Addition bumps patch`` () =
    let v =
        { Major = 0
          Minor = 2
          Patch = 3
          Stage = Stable }

    test <@ determineBump v (Addition(ApiSignature "added", [])) = bumpPatch v @>

// determineBump: Alpha

[<Fact>]
let ``alpha + Breaking increments alpha number`` () =
    let v =
        { Major = 0
          Minor = 1
          Patch = 0
          Stage = PreRelease(Alpha 1) }

    test <@ determineBump v (Breaking(ApiSignature "removed", [])) = { v with Stage = PreRelease(Alpha 2) } @>

[<Fact>]
let ``alpha + Addition increments alpha number`` () =
    let v =
        { Major = 0
          Minor = 1
          Patch = 0
          Stage = PreRelease(Alpha 3) }

    test <@ determineBump v (Addition(ApiSignature "added", [])) = { v with Stage = PreRelease(Alpha 4) } @>

[<Fact>]
let ``alpha + NoChange increments alpha number`` () =
    let v =
        { Major = 0
          Minor = 1
          Patch = 0
          Stage = PreRelease(Alpha 2) }

    test <@ determineBump v NoChange = { v with Stage = PreRelease(Alpha 3) } @>

// determineBump: Beta

[<Fact>]
let ``beta + any change increments beta number`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(Beta 1) }

    test <@ determineBump v (Breaking(ApiSignature "removed", [])) = { v with Stage = PreRelease(Beta 2) } @>

// determineBump: RC

[<Fact>]
let ``RC + NoChange promotes to stable`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(RC 1) }

    test <@ determineBump v NoChange = toStable v @>

[<Fact>]
let ``RC + API change reverts to beta`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(RC 1) }

    test <@ determineBump v (Breaking(ApiSignature "removed", [])) = toBeta v @>

[<Fact>]
let ``RC + Addition reverts to beta`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(RC 2) }

    test <@ determineBump v (Addition(ApiSignature "added", [])) = toBeta v @>

// forCommand

[<Fact>]
let ``forCommand StartAlpha + FirstRelease returns firstAlpha`` () =
    test <@ forCommand FirstRelease StartAlpha = Ok firstAlpha @>

[<Fact>]
let ``forCommand StartAlpha + HasPreviousRelease returns nextAlphaCycle`` () =
    let v =
        { Major = 0
          Minor = 1
          Patch = 0
          Stage = Stable }

    test <@ forCommand (HasPreviousRelease v) StartAlpha = Ok(nextAlphaCycle v) @>

[<Fact>]
let ``forCommand PromoteToBeta`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(Alpha 3) }

    test <@ forCommand (HasPreviousRelease v) PromoteToBeta = Ok(toBeta v) @>

[<Fact>]
let ``forCommand PromoteToRC`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(Beta 2) }

    test <@ forCommand (HasPreviousRelease v) PromoteToRC = Ok(toRC v) @>

[<Fact>]
let ``forCommand PromoteToStable`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(RC 1) }

    test <@ forCommand (HasPreviousRelease v) PromoteToStable = Ok(toStable v) @>

[<Fact>]
let ``forCommand PromoteToBeta + FirstRelease returns Error`` () =
    test
        <@
            match forCommand FirstRelease PromoteToBeta with
            | Error msg -> msg.Contains("Cannot")
            | Ok _ -> false
        @>

[<Fact>]
let ``forCommand PromoteToRC + FirstRelease returns Error`` () =
    test
        <@
            match forCommand FirstRelease PromoteToRC with
            | Error msg -> msg.Contains("Cannot")
            | Ok _ -> false
        @>

[<Fact>]
let ``forCommand PromoteToStable + FirstRelease returns Error`` () =
    test
        <@
            match forCommand FirstRelease PromoteToStable with
            | Error msg -> msg.Contains("Cannot")
            | Ok _ -> false
        @>

[<Fact>]
let ``forCommand Auto returns Error`` () =
    test
        <@
            match forCommand FirstRelease Auto with
            | Error msg -> msg.Contains("Auto")
            | Ok _ -> false
        @>

[<Fact>]
let ``forCommand Auto with HasPreviousRelease returns Error`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = Stable }

    test
        <@
            match forCommand (HasPreviousRelease v) Auto with
            | Error msg -> msg.Contains("Auto")
            | Ok _ -> false
        @>

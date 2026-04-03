module FsSemanticTagger.Tests.VersionTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Version

[<Fact>]
let ``parse stable version`` () =
    test
        <@
            parse "1.2.3" = { Major = 1
                              Minor = 2
                              Patch = 3
                              Stage = Stable }
        @>

[<Fact>]
let ``parse alpha pre-release`` () =
    test
        <@
            parse "0.1.0-alpha.1" = { Major = 0
                                      Minor = 1
                                      Patch = 0
                                      Stage = PreRelease(Alpha 1) }
        @>

[<Fact>]
let ``parse beta pre-release`` () =
    test
        <@
            parse "1.0.0-beta.2" = { Major = 1
                                     Minor = 0
                                     Patch = 0
                                     Stage = PreRelease(Beta 2) }
        @>

[<Fact>]
let ``parse rc pre-release`` () =
    test
        <@
            parse "2.0.0-rc.1" = { Major = 2
                                   Minor = 0
                                   Patch = 0
                                   Stage = PreRelease(RC 1) }
        @>

[<Fact>]
let ``format roundtrips stable`` () =
    test <@ format (parse "1.2.3") = "1.2.3" @>

[<Fact>]
let ``format roundtrips alpha`` () =
    test <@ format (parse "0.1.0-alpha.1") = "0.1.0-alpha.1" @>

[<Fact>]
let ``format roundtrips beta`` () =
    test <@ format (parse "1.0.0-beta.2") = "1.0.0-beta.2" @>

[<Fact>]
let ``format roundtrips rc`` () =
    test <@ format (parse "2.0.0-rc.1") = "2.0.0-rc.1" @>

[<Fact>]
let ``toTag with simple prefix`` () =
    test
        <@
            toTag
                "v"
                { Major = 1
                  Minor = 2
                  Patch = 3
                  Stage = Stable } = "v1.2.3"
        @>

[<Fact>]
let ``toTag with compound prefix and pre-release`` () =
    test
        <@
            toTag
                "core-v"
                { Major = 0
                  Minor = 1
                  Patch = 0
                  Stage = PreRelease(Alpha 1) } = "core-v0.1.0-alpha.1"
        @>

[<Fact>]
let ``bumpPatch increments patch`` () =
    test
        <@
            bumpPatch
                { Major = 1
                  Minor = 2
                  Patch = 3
                  Stage = Stable } = { Major = 1
                                       Minor = 2
                                       Patch = 4
                                       Stage = Stable }
        @>

[<Fact>]
let ``bumpMinor increments minor and resets patch`` () =
    test
        <@
            bumpMinor
                { Major = 1
                  Minor = 2
                  Patch = 3
                  Stage = Stable } = { Major = 1
                                       Minor = 3
                                       Patch = 0
                                       Stage = Stable }
        @>

[<Fact>]
let ``bumpMajor increments major and resets minor and patch`` () =
    test
        <@
            bumpMajor
                { Major = 1
                  Minor = 2
                  Patch = 3
                  Stage = Stable } = { Major = 2
                                       Minor = 0
                                       Patch = 0
                                       Stage = Stable }
        @>

[<Fact>]
let ``bumpPreRelease alpha`` () =
    test <@ bumpPreRelease (Alpha 1) = Alpha 2 @>

[<Fact>]
let ``bumpPreRelease beta`` () =
    test <@ bumpPreRelease (Beta 3) = Beta 4 @>

[<Fact>]
let ``bumpPreRelease rc`` () = test <@ bumpPreRelease (RC 1) = RC 2 @>

[<Fact>]
let ``nextAlphaCycle bumps minor and starts alpha.1`` () =
    test
        <@
            nextAlphaCycle
                { Major = 0
                  Minor = 1
                  Patch = 3
                  Stage = Stable } = { Major = 0
                                       Minor = 2
                                       Patch = 0
                                       Stage = PreRelease(Alpha 1) }
        @>

[<Fact>]
let ``toBeta sets stage to Beta 1`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(Alpha 3) }

    test <@ toBeta v = { v with Stage = PreRelease(Beta 1) } @>

[<Fact>]
let ``toRC sets stage to RC 1`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(Beta 2) }

    test <@ toRC v = { v with Stage = PreRelease(RC 1) } @>

[<Fact>]
let ``toStable removes pre-release stage`` () =
    let v =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = PreRelease(RC 1) }

    test <@ toStable v = { v with Stage = Stable } @>

[<Fact>]
let ``firstAlpha is 0.1.0-alpha.1`` () =
    test
        <@
            firstAlpha = { Major = 0
                           Minor = 1
                           Patch = 0
                           Stage = PreRelease(Alpha 1) }
        @>

[<Fact>]
let ``sortKey alpha.1 less than alpha.2`` () =
    let a = sortKey (parse "0.1.0-alpha.1")
    let b = sortKey (parse "0.1.0-alpha.2")
    test <@ a < b @>

[<Fact>]
let ``sortKey alpha less than beta`` () =
    let a = sortKey (parse "0.1.0-alpha.2")
    let b = sortKey (parse "0.1.0-beta.1")
    test <@ a < b @>

[<Fact>]
let ``sortKey beta less than rc`` () =
    let a = sortKey (parse "0.1.0-beta.1")
    let b = sortKey (parse "0.1.0-rc.1")
    test <@ a < b @>

[<Fact>]
let ``sortKey rc less than stable`` () =
    let a = sortKey (parse "0.1.0-rc.1")
    let b = sortKey (parse "0.1.0")
    test <@ a < b @>

module SyncDocs.Tests.AttributesTests

open Xunit
open Tests.Common
open Swensen.Unquote

[<Fact>]
let ``computeTimeoutMs - null returns 1000`` () = test <@ computeTimeoutMs null = 1000 @>

[<Fact>]
let ``computeTimeoutMs - empty returns 1000`` () = test <@ computeTimeoutMs "" = 1000 @>

[<Fact>]
let ``computeTimeoutMs - "false" returns 1000`` () =
    test <@ computeTimeoutMs "false" = 1000 @>

[<Fact>]
let ``computeTimeoutMs - "true" returns 5000`` () =
    test <@ computeTimeoutMs "true" = 5000 @>

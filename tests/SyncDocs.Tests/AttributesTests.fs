module SyncDocs.Tests.AttributesTests

open Xunit
open Tests.Common
open Swensen.Unquote

[<Fact>]
let ``computeTimeoutMs - null returns 5000 (local)`` () = test <@ computeTimeoutMs null = 5000 @>

[<Fact>]
let ``computeTimeoutMs - empty returns 5000 (local)`` () = test <@ computeTimeoutMs "" = 5000 @>

[<Fact>]
let ``computeTimeoutMs - "false" returns 5000 (local)`` () =
    test <@ computeTimeoutMs "false" = 5000 @>

[<Fact>]
let ``computeTimeoutMs - "true" returns 10000 (CI)`` () =
    test <@ computeTimeoutMs "true" = 10000 @>

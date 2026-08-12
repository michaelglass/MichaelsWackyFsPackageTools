namespace Tests.Common

open System

[<AutoOpen>]
[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module Attributes =
    // Per-test timeout in ms. A 1000ms local budget flakily cancelled
    // legitimately-slow tests (real `dotnet restore` probes, reflection-based
    // assembly loads) whenever the machine was under parallel-collection or
    // background load. Local machines can be just as loaded as CI, so both get
    // room: 5000ms locally, 10000ms in CI for slower runners.
    let computeTimeoutMs (ci: string) =
        if String.IsNullOrEmpty(ci) || ci = "false" then
            5000
        else
            10000

    let defaultTimeoutMs = computeTimeoutMs (Environment.GetEnvironmentVariable("CI"))

    type FactAttribute() =
        inherit Xunit.FactAttribute(Timeout = defaultTimeoutMs)

    type TheoryAttribute() =
        inherit Xunit.TheoryAttribute(Timeout = defaultTimeoutMs)

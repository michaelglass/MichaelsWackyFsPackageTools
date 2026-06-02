namespace Tests.Common

open System

[<AutoOpen>]
[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module Attributes =
    // Per-test timeout in ms. The local (non-CI) budget used to be 1000ms, but
    // that flakily cancelled legitimately-slow tests (real `dotnet restore`
    // probes, reflection-based assembly loads) whenever the machine was under
    // parallel-collection or background load — surfacing as "Test execution
    // timed out after 1000 milliseconds". Local machines can be just as loaded
    // as CI, so neither gets the old tight budget: 5000ms locally (the value CI
    // already ran reliably) and 10000ms in CI for headroom on slower runners.
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

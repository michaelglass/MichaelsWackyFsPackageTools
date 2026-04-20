namespace Tests.Common

open System

[<AutoOpen>]
[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module Attributes =
    let computeTimeoutMs (ci: string) =
        if String.IsNullOrEmpty(ci) || ci = "false" then 1000 else 5000

    let defaultTimeoutMs =
        computeTimeoutMs (Environment.GetEnvironmentVariable("CI"))

    type FactAttribute() =
        inherit Xunit.FactAttribute(Timeout = defaultTimeoutMs)

    type TheoryAttribute() =
        inherit Xunit.TheoryAttribute(Timeout = defaultTimeoutMs)

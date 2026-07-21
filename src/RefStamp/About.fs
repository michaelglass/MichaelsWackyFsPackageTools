namespace RefStamp

/// RefStamp ships no runtime code: the package's payload is the MSBuild pair
/// <c>build/RefStamp.props</c> + <c>build/RefStamp.targets</c>, which NuGet
/// imports into every referencing project. This module exists only because an
/// F# project must compile at least one file; the placeholder assembly is not
/// packed (<c>IncludeBuildOutput=false</c>).
module About =

    /// The package id, for tooling that wants to name the guard.
    [<Literal>]
    let PackageId = "RefStamp"

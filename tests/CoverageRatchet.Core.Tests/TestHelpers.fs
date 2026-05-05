module CoverageRatchet.Core.Tests.TestHelpers

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let makeFile name linePct branchPct branchesCovered branchesTotal : FileCoverage =
    { FileName = name
      LinePct = linePct
      BranchPct = branchPct
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

let defaultsConfig: Config =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      Overrides = Map.empty }

let otherPlatform =
    match Platform.current with
    | MacOS -> Linux
    | Linux -> Windows
    | Windows -> MacOS

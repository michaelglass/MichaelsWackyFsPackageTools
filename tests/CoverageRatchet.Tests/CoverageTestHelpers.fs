module CoverageRatchet.Tests.CoverageTestHelpers

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let makeFile name linePct branchPct branchesCovered branchesTotal =
    { FileName = name
      LinePct = linePct
      BranchPct = branchPct
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

let defaultsConfig =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      Overrides = Map.empty }

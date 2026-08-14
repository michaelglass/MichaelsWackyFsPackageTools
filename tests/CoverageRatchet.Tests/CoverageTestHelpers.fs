module CoverageRatchet.Tests.CoverageTestHelpers

open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

/// Percentage-focused fixture. Line counts are synthesised out of 100 so that
/// LinesCovered/LinesTotal stay consistent with linePct. Use `makeFileWithCounts`
/// when the absolute covered-line count is what's under test.
let makeFile name linePct branchPct branchesCovered branchesTotal : FileCoverage =
    { FileName = name
      LinePct = linePct
      BranchPct = branchPct
      LinesCovered = int (round (linePct: float))
      LinesTotal = 100
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

/// Count-focused fixture: state the absolute counts, percentages are derived.
let makeFileWithCounts name linesCovered linesTotal branchesCovered branchesTotal : FileCoverage =
    { FileName = name
      LinePct =
        if linesTotal > 0 then
            float linesCovered / float linesTotal * 100.0
        else
            100.0
      BranchPct =
        if branchesTotal > 0 then
            float branchesCovered / float branchesTotal * 100.0
        else
            100.0
      LinesCovered = linesCovered
      LinesTotal = linesTotal
      BranchesCovered = branchesCovered
      BranchesTotal = branchesTotal }

let defaultsConfig: Config =
    { DefaultLine = 100.0
      DefaultBranch = 100.0
      Overrides = Map.empty
      CountFloors = Map.empty }

let countFloor coveredLines coveredBranches : CountFloor =
    { CoveredLines = coveredLines
      CoveredBranches = coveredBranches
      Reason = None
      Platform = None }

let otherPlatform =
    match Platform.current with
    | MacOS -> Linux
    | Linux -> Windows
    | Windows -> MacOS

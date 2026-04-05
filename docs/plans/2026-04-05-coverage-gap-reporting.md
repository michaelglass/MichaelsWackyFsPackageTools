# CoverageRatchet: Machine-Readable Coverage Gap Reporting

**Status:** Future work

**Goal:** The ratchet tool should surface clear, Claude-readable coverage gaps per file — both branch and line gaps — so that an LLM can act on them directly to improve coverage.

**Motivation:** Today, understanding what's uncovered requires parsing raw Cobertura XML. The ratchet tool already knows which files fail and their coverage percentages, but doesn't report *which specific lines/branches* are uncovered.

**Possible output format:**
```
FAIL Program.fs: line=85% branch=89%
  uncovered lines: 43, 44, 131, 139, 141-142, 144-145, 147-148, 150-151
  uncovered branches: 135 (if Array.isEmpty argv), 87 (branch comparison)
```

This would let a coding agent read the output and write targeted tests without needing to parse XML.

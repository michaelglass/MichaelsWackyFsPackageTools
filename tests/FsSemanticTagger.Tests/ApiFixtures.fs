// Before/after pairs of a public API, compiled for real so the API differ sees
// the exact metadata the F# compiler emits. Each scenario lives in its own
// `…Before` / `…After` namespace pair; a test extracts one namespace's types and
// renames it to a common name, so the two sides diff as one library's releases.

namespace ApiFixtures.NewCaseWithFields.Before

type Shape =
    | Circle of radius: float
    | Square of side: float

namespace ApiFixtures.NewCaseWithFields.After

type Shape =
    | Circle of radius: float
    | Square of side: float
    | Triangle of a: float * b: float * c: float

namespace ApiFixtures.NewNullaryCase.Before

type Platform =
    | Linux
    | MacOS

namespace ApiFixtures.NewNullaryCase.After

type Platform =
    | Linux
    | MacOS
    | Windows

namespace ApiFixtures.SingleCaseUnion.Before

type Token = Token of string

namespace ApiFixtures.SingleCaseUnion.After

type Token =
    | Token of string
    | Anonymous

namespace ApiFixtures.QualifiedAccessUnion.Before

[<RequireQualifiedAccess>]
type Mode =
    | Fast
    | Safe of retries: int

namespace ApiFixtures.QualifiedAccessUnion.After

[<RequireQualifiedAccess>]
type Mode =
    | Fast
    | Safe of retries: int
    | Custom of name: string

namespace ApiFixtures.StructUnion.Before

[<Struct>]
type Outcome =
    | Passed of score: int
    | Skipped of reason: string

namespace ApiFixtures.StructUnion.After

[<Struct>]
type Outcome =
    | Passed of score: int
    | Skipped of reason: string
    | Errored of code: int

namespace ApiFixtures.UnionInModule.Before

module Ratchet =
    type Status =
        | NoChanges
        | Tightened of files: string list

namespace ApiFixtures.UnionInModule.After

module Ratchet =
    type Status =
        | NoChanges
        | Tightened of files: string list
        | Failed of files: string list

// The CoverageRatchet.Core shape that was misreported as breaking: types and a
// function added inside a module that already existed.
namespace ApiFixtures.TypeInModule.Before

module Cobertura =
    type FileCoverage = { Path: string; Lines: int }

    let parseXml (_xml: string) : FileCoverage list = []

namespace ApiFixtures.TypeInModule.After

module Cobertura =
    type FileCoverage = { Path: string; Lines: int }

    type ReaderOptions = { StripPrefix: string option }

    type ExclusionReason =
        | Generated
        | Matched of pattern: string

    let parseXml (_xml: string) : FileCoverage list = []

    let readReports (_options: ReaderOptions) (_paths: string list) : FileCoverage list = []

// A union whose representation is private: consumers cannot match on its cases,
// so a new case does not break them.
namespace ApiFixtures.PrivateUnion.Before

type Handle =
    private
    | Open of int
    | Closed

namespace ApiFixtures.PrivateUnion.After

type Handle =
    private
    | Open of int
    | Closed
    | Pending

// A brand-new union: its cases are new along with it, so nothing can break.
namespace ApiFixtures.NewTopLevelUnion.Before

type Existing = { Name: string }

namespace ApiFixtures.NewTopLevelUnion.After

type Existing = { Name: string }

type Fresh =
    | First of int
    | Second

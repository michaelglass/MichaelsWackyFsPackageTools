module FsSemanticTagger.Version

open System.Text.RegularExpressions

type PreRelease =
    | Alpha of int
    | Beta of int
    | RC of int

type VersionStage =
    | PreRelease of PreRelease
    | Stable

type Version =
    { Major: int
      Minor: int
      Patch: int
      Stage: VersionStage }

let private versionRegex =
    Regex(@"^(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?$", RegexOptions.Compiled)

let tryParse (s: string) : Result<Version, string> =
    let m = versionRegex.Match(s)

    if not m.Success then
        Error $"Invalid version string: {s}"
    else
        let major = int m.Groups[1].Value
        let minor = int m.Groups[2].Value
        let patch = int m.Groups[3].Value

        let stage =
            if m.Groups[4].Success then
                let n = int m.Groups[5].Value

                match m.Groups[4].Value with
                | "alpha" -> Ok(PreRelease(Alpha n))
                | "beta" -> Ok(PreRelease(Beta n))
                | "rc" -> Ok(PreRelease(RC n))
                | other -> Error $"Unknown pre-release stage: {other}"
            else
                Ok Stable

        stage
        |> Result.map (fun s ->
            { Major = major
              Minor = minor
              Patch = patch
              Stage = s })

let parse (s: string) : Version =
    match tryParse s with
    | Ok v -> v
    | Error msg -> failwith msg

let format (v: Version) : string =
    let base_ = $"{v.Major}.{v.Minor}.{v.Patch}"

    match v.Stage with
    | Stable -> base_
    | PreRelease(Alpha n) -> $"{base_}-alpha.{n}"
    | PreRelease(Beta n) -> $"{base_}-beta.{n}"
    | PreRelease(RC n) -> $"{base_}-rc.{n}"

let toTag (prefix: string) (v: Version) : string = prefix + format v

let firstAlpha =
    { Major = 0
      Minor = 1
      Patch = 0
      Stage = PreRelease(Alpha 1) }

let bumpPreRelease (pre: PreRelease) : PreRelease =
    match pre with
    | Alpha n -> Alpha(n + 1)
    | Beta n -> Beta(n + 1)
    | RC n -> RC(n + 1)

let nextAlphaCycle (v: Version) : Version =
    { v with
        Minor = v.Minor + 1
        Patch = 0
        Stage = PreRelease(Alpha 1) }

let toBeta (v: Version) : Version = { v with Stage = PreRelease(Beta 1) }

let toRC (v: Version) : Version = { v with Stage = PreRelease(RC 1) }

let toStable (v: Version) : Version = { v with Stage = Stable }

let bumpPatch (v: Version) : Version = { v with Patch = v.Patch + 1 }

let bumpMinor (v: Version) : Version =
    { v with
        Minor = v.Minor + 1
        Patch = 0 }

let bumpMajor (v: Version) : Version =
    { v with
        Major = v.Major + 1
        Minor = 0
        Patch = 0 }

let sortKey (v: Version) =
    let stageKey =
        match v.Stage with
        | PreRelease(Alpha n) -> (0, n)
        | PreRelease(Beta n) -> (1, n)
        | PreRelease(RC n) -> (2, n)
        | Stable -> (3, 0)

    (v.Major, v.Minor, v.Patch, stageKey)

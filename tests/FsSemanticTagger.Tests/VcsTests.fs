module FsSemanticTagger.Tests.VcsTests

open Xunit
open Swensen.Unquote
open FsSemanticTagger.Shell
open FsSemanticTagger.Vcs
open FsSemanticTagger.Version

let fakeRun (responses: (string * string * CommandResult) list) : string -> string -> CommandResult =
    let mutable calls = []

    fun cmd args ->
        calls <- calls @ [ (cmd, args) ]

        responses
        |> List.tryFind (fun (c, a, _) -> c = cmd && a = args)
        |> Option.map (fun (_, _, r) -> r)
        |> Option.defaultValue (Failure(sprintf "unexpected call: %s %s" cmd args))

// hasUncommittedChanges

[<Fact>]
let ``hasUncommittedChanges - clean working copy returns false`` () =
    let run = fakeRun [ ("jj", "status", Success "The working copy is clean") ]
    test <@ hasUncommittedChanges run = false @>

[<Fact>]
let ``hasUncommittedChanges - no changes message returns false`` () =
    let run = fakeRun [ ("jj", "status", Success "The working copy has no changes.") ]

    test <@ hasUncommittedChanges run = false @>

[<Fact>]
let ``hasUncommittedChanges - dirty working copy returns true`` () =
    let run =
        fakeRun [ ("jj", "status", Success "Working copy changes:\nM src/Foo.fs") ]

    test <@ hasUncommittedChanges run = true @>

[<Fact>]
let ``hasUncommittedChanges - jj failure returns true`` () =
    let run = fakeRun [ ("jj", "status", Failure "not a jj repo") ]
    test <@ hasUncommittedChanges run = true @>

// tagExists

[<Fact>]
let ``tagExists - jj finds tag returns true`` () =
    let run = fakeRun [ ("jj", "tag list v1.0.0", Success "v1.0.0") ]
    test <@ tagExists run "v1.0.0" = true @>

[<Fact>]
let ``tagExists - jj fails but git finds tag returns true`` () =
    let run =
        fakeRun
            [ ("jj", "tag list v1.0.0", Failure "no jj")
              ("git", "tag -l v1.0.0", Success "v1.0.0") ]

    test <@ tagExists run "v1.0.0" = true @>

[<Fact>]
let ``tagExists - neither jj nor git finds tag returns false`` () =
    let run =
        fakeRun
            [ ("jj", "tag list v1.0.0", Failure "no jj")
              ("git", "tag -l v1.0.0", Success "") ]

    test <@ tagExists run "v1.0.0" = false @>

// getLatestTag

let jjTagListArgs prefix =
    sprintf "tag list \"glob:%s*\" -T \"name ++ \\\"\\n\\\"\"" prefix

[<Fact>]
let ``getLatestTag - jj finds latest by version sort`` () =
    let run = fakeRun [ ("jj", jjTagListArgs "v", Success "v1.0.0\nv1.2.0\nv1.1.0") ]

    test <@ getLatestTag run "v" = Some "v1.2.0" @>

[<Fact>]
let ``getLatestTag - jj whitespace-only falls back to git`` () =
    let run =
        fakeRun
            [ ("jj", jjTagListArgs "v", Success "  \n  ")
              ("git", "tag -l \"v*\"", Success "v1.0.0") ]

    test <@ getLatestTag run "v" = Some "v1.0.0" @>

[<Fact>]
let ``getLatestTag - jj failure falls back to git`` () =
    let run =
        fakeRun
            [ ("jj", jjTagListArgs "v", Failure "no jj")
              ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.2.0\nv1.1.0") ]

    test <@ getLatestTag run "v" = Some "v1.2.0" @>

[<Fact>]
let ``getLatestTag - no tags returns None`` () =
    let run =
        fakeRun [ ("jj", jjTagListArgs "v", Success ""); ("git", "tag -l \"v*\"", Success "") ]

    test <@ getLatestTag run "v" = None @>

[<Fact>]
let ``getLatestTag - both fail returns None`` () =
    let run =
        fakeRun
            [ ("jj", jjTagListArgs "v", Failure "no jj")
              ("git", "tag -l \"v*\"", Failure "not a git repo") ]

    test <@ getLatestTag run "v" = None @>

[<Fact>]
let ``getLatestTag - skips unparseable tags and returns latest valid`` () =
    let run = fakeRun [ ("jj", jjTagListArgs "v", Success "v-bad\nv1.0.0\nv2.0.0") ]

    test <@ getLatestTag run "v" = Some "v2.0.0" @>

// commitAndTag

[<Fact>]
let ``commitAndTag - uses jj tag when available`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", a when a.StartsWith("commit") -> Success ""
        | "jj", a when a.StartsWith("tag set") -> Success ""
        | _ -> Failure "unexpected"

    let version =
        { Major = 1
          Minor = 0
          Patch = 0
          Stage = Stable }

    let tag = commitAndTag run "v" version
    test <@ tag = "v1.0.0" @>
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "tag set v1.0.0") @>
    test <@ not (calls |> List.exists (fun (c, _) -> c = "git")) @>

[<Fact>]
let ``commitAndTag - falls back to git tag when jj tag fails`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", a when a.StartsWith("commit") -> Success ""
        | "jj", a when a.StartsWith("tag set") -> Failure "jj tag not supported"
        | "git", a when a.StartsWith("tag") -> Success ""
        | _ -> Failure "unexpected"

    let version =
        { Major = 2
          Minor = 1
          Patch = 0
          Stage = Stable }

    let tag = commitAndTag run "v" version
    test <@ tag = "v2.1.0" @>

    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.StartsWith("tag -a v2.1.0")) @>

// getCurrentCommitSha

[<Fact>]
let ``getCurrentCommitSha - gets sha from jj`` () =
    let run =
        fakeRun [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123def") ]

    test <@ getCurrentCommitSha run = Some "abc123def" @>

[<Fact>]
let ``getCurrentCommitSha - falls back to git when jj fails`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "not a jj repo")
              ("git", "rev-parse HEAD", Success "def456abc") ]

    test <@ getCurrentCommitSha run = Some "def456abc" @>

[<Fact>]
let ``getCurrentCommitSha - returns None when both fail`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "no jj")
              ("git", "rev-parse HEAD", Failure "no git") ]

    test <@ getCurrentCommitSha run = None @>

// isCiPassing

let ghCiArgs sha =
    sprintf "run list --commit %s --json status,conclusion,name,url" sha

[<Fact>]
let ``isCiPassing - returns true when all runs succeed`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh",
               ghCiArgs "abc123",
               Success
                   """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"completed","conclusion":"success","name":"Deploy","url":"https://example.com/2"}]""") ]

    test <@ isCiPassing run = true @>

[<Fact>]
let ``isCiPassing - returns false when any run fails`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh",
               ghCiArgs "abc123",
               Success
                   """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"completed","conclusion":"failure","name":"Deploy","url":"https://example.com/2"}]""") ]

    test <@ isCiPassing run = false @>

[<Fact>]
let ``isCiPassing - returns false when no runs exist`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]") ]

    test <@ isCiPassing run = false @>

[<Fact>]
let ``isCiPassing - returns false when gh fails`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Failure "gh not installed") ]

    test <@ isCiPassing run = false @>

[<Fact>]
let ``isCiPassing - returns false when commit sha unavailable`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "no jj")
              ("git", "rev-parse HEAD", Failure "no git") ]

    test <@ isCiPassing run = false @>

// parseCiRuns

[<Fact>]
let ``parseCiRuns - parses completed success runs`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let runs = parseCiRuns json
    test <@ runs.Length = 1 @>
    test <@ runs.[0].Name = "CI" @>
    test <@ runs.[0].Status = "completed" @>
    test <@ runs.[0].Conclusion = "success" @>
    test <@ runs.[0].Url = "https://example.com/1" @>

[<Fact>]
let ``parseCiRuns - parses multiple runs with mixed status`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"in_progress","conclusion":"","name":"Deploy","url":"https://example.com/2"}]"""

    let runs = parseCiRuns json
    test <@ runs.Length = 2 @>
    test <@ runs.[1].Status = "in_progress" @>

[<Fact>]
let ``parseCiRuns - handles empty array`` () =
    let runs = parseCiRuns "[]"
    test <@ runs.Length = 0 @>

[<Fact>]
let ``parseCiRuns - handles null conclusion for in-progress`` () =
    let json =
        """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""

    let runs = parseCiRuns json
    test <@ runs.[0].Conclusion = "" @>

// checkCiStatusForSha

let ghCiJsonArgs sha =
    sprintf "run list --commit %s --json status,conclusion,name,url" sha

[<Fact>]
let ``checkCiStatusForSha - all success returns Passed`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Success json) ]
    test <@ checkCiStatusForSha run "abc123" = Passed @>

[<Fact>]
let ``checkCiStatusForSha - any failure returns Failed with failed runs`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"completed","conclusion":"failure","name":"Deploy","url":"https://example.com/2"}]"""

    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | Failed runs -> runs.Length = 1 && runs.[0].Name = "Deploy"
            | _ -> false
        @>

[<Fact>]
let ``checkCiStatusForSha - in_progress with no failures returns InProgress`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"in_progress","conclusion":null,"name":"Deploy","url":"https://example.com/2"}]"""

    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

[<Fact>]
let ``checkCiStatusForSha - no runs returns NoRuns`` () =
    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Success "[]") ]
    test <@ checkCiStatusForSha run "abc123" = NoRuns @>

[<Fact>]
let ``checkCiStatusForSha - gh failure returns Unknown`` () =
    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Failure "gh not found") ]
    test <@ checkCiStatusForSha run "abc123" = Unknown @>

[<Fact>]
let ``checkCiStatusForSha - cancelled treated as failure`` () =
    let json =
        """[{"status":"completed","conclusion":"cancelled","name":"CI","url":"https://example.com/1"}]"""

    let run = fakeRun [ ("gh", ghCiJsonArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | Failed runs -> runs.Length = 1
            | _ -> false
        @>

// getCiStatus

[<Fact>]
let ``getCiStatus - returns status for current commit`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiJsonArgs "abc123", Success json) ]

    test <@ getCiStatus run = Passed @>

[<Fact>]
let ``getCiStatus - falls back to parent when working copy clean and current has no runs`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiJsonArgs "abc123", Success "[]")
              ("jj", "status", Success "The working copy is clean")
              ("jj", "log -r @- --no-graph -T commit_id", Success "def456")
              ("gh", ghCiJsonArgs "def456", Success json) ]

    test <@ getCiStatus run = Passed @>

[<Fact>]
let ``getCiStatus - returns Unknown when no commit sha`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "no jj")
              ("git", "rev-parse HEAD", Failure "no git") ]

    test <@ getCiStatus run = Unknown @>

[<Fact>]
let ``getCiStatus - returns InProgress without parent fallback`` () =
    let json =
        """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiJsonArgs "abc123", Success json) ]

    test
        <@
            match getCiStatus run with
            | InProgress _ -> true
            | _ -> false
        @>

// pushTags

[<Fact>]
let ``pushTags - exports and pushes each tag`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "git export" -> Success ""
        | "git", a when a.StartsWith("push origin") -> Success ""
        | _ -> Failure "unexpected"

    pushTags run [ "v1.0.0"; "v2.0.0" ]
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git export") @>

    test
        <@
            calls
            |> List.exists (fun (c, a) -> c = "git" && a = "push origin v1.0.0 v2.0.0")
        @>

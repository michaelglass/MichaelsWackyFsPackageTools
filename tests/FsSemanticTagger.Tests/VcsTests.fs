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

// tagRevision

[<Fact>]
let ``tagRevision - sets tag on specified revision via jj`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", a when a.StartsWith("tag set") -> Success ""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    tagRevision run "v1.0.0" "main"
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "tag set v1.0.0 -r main") @>

[<Fact>]
let ``tagRevision - falls back to git tag when jj fails`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", a when a.StartsWith("tag set") -> Failure "jj tag not supported"
        | "git", a when a.StartsWith("tag") -> Success ""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    tagRevision run "v1.0.0" "main"
    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a.Contains("tag -a v1.0.0")) @>

// commitAndAdvanceMain

[<Fact>]
let ``commitAndAdvanceMain - commits and moves main bookmark`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", a when a.StartsWith("commit") -> Success ""
        | "jj", a when a.StartsWith("bookmark set") -> Success ""
        | _ -> Failure(sprintf "unexpected: %s %s" cmd args)

    commitAndAdvanceMain run "Bump versions"

    test
        <@
            calls
            |> List.exists (fun (c, a) -> c = "jj" && a.Contains("commit") && a.Contains("Bump versions"))
        @>

    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a.Contains("bookmark set main")) @>

// hasChangesSinceTag

[<Fact>]
let ``hasChangesSinceTag - returns true when files changed in path`` () =
    let run =
        fakeRun
            [ ("jj",
               "diff --from v1.0.0 --to @ \"glob:src/MyLib/**\"",
               Success "diff --git a/src/MyLib/Lib.fs b/src/MyLib/Lib.fs\n...") ]

    test <@ hasChangesSinceTag run "v1.0.0" "src/MyLib" = true @>

[<Fact>]
let ``hasChangesSinceTag - returns false when no files changed in path`` () =
    // jj diff (without --stat) returns empty string when no changes
    let run =
        fakeRun [ ("jj", "diff --from v1.0.0 --to @ \"glob:src/MyLib/**\"", Success "") ]

    test <@ hasChangesSinceTag run "v1.0.0" "src/MyLib" = false @>

[<Fact>]
let ``hasChangesSinceTag - returns true when jj command fails`` () =
    let run =
        fakeRun [ ("jj", "diff --from v1.0.0 --to @ \"glob:src/MyLib/**\"", Failure "unknown tag") ]

    // Conservative: if we can't tell, assume changes
    test <@ hasChangesSinceTag run "v1.0.0" "src/MyLib" = true @>

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
    test <@ runs.[0].Status = Completed @>
    test <@ runs.[0].Conclusion = SuccessConclusion @>
    test <@ runs.[0].Url = "https://example.com/1" @>

[<Fact>]
let ``parseCiRuns - parses multiple runs with mixed status`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"in_progress","conclusion":"","name":"Deploy","url":"https://example.com/2"}]"""

    let runs = parseCiRuns json
    test <@ runs.Length = 2 @>
    test <@ runs.[1].Status = InProgressStatus @>

[<Fact>]
let ``parseCiRuns - handles empty array`` () =
    let runs = parseCiRuns "[]"
    test <@ runs.Length = 0 @>

[<Fact>]
let ``parseCiRuns - handles null conclusion for in-progress`` () =
    let json =
        """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""

    let runs = parseCiRuns json
    test <@ runs.[0].Conclusion = PendingConclusion @>

// checkCiStatusForSha

[<Fact>]
let ``checkCiStatusForSha - all success returns Passed`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
    test <@ checkCiStatusForSha run "abc123" = Passed @>

[<Fact>]
let ``checkCiStatusForSha - any failure returns Failed with failed runs`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"completed","conclusion":"failure","name":"Deploy","url":"https://example.com/2"}]"""

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
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

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

[<Fact>]
let ``checkCiStatusForSha - no runs returns NoRuns`` () =
    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success "[]") ]
    test <@ checkCiStatusForSha run "abc123" = NoRuns @>

[<Fact>]
let ``checkCiStatusForSha - gh failure returns Unknown`` () =
    let run = fakeRun [ ("gh", ghCiArgs "abc123", Failure "gh not found") ]
    test <@ checkCiStatusForSha run "abc123" = Unknown @>

[<Fact>]
let ``checkCiStatusForSha - cancelled treated as failure`` () =
    let json =
        """[{"status":"completed","conclusion":"cancelled","name":"CI","url":"https://example.com/1"}]"""

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
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
              ("gh", ghCiArgs "abc123", Success json) ]

    test <@ getCiStatus run = Passed @>

[<Fact>]
let ``getCiStatus - falls back to parent when working copy clean and current has no runs`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]")
              ("jj", "status", Success "The working copy is clean")
              ("jj", "log -r @- --no-graph -T commit_id", Success "def456")
              ("gh", ghCiArgs "def456", Success json) ]

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
              ("gh", ghCiArgs "abc123", Success json) ]

    test
        <@
            match getCiStatus run with
            | InProgress _ -> true
            | _ -> false
        @>

// pushTags

[<Fact>]
let ``pushMain - pushes main bookmark via jj`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "git push" -> Success ""
        | _ -> Failure "unexpected"

    pushMain run
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git push") @>

// RunStatus.ofString

[<Fact>]
let ``RunStatus.ofString completed`` () =
    test <@ RunStatus.ofString "completed" = Completed @>

[<Fact>]
let ``RunStatus.ofString in_progress`` () =
    test <@ RunStatus.ofString "in_progress" = InProgressStatus @>

[<Fact>]
let ``RunStatus.ofString queued`` () =
    test <@ RunStatus.ofString "queued" = Queued @>

[<Fact>]
let ``RunStatus.ofString unknown value`` () =
    test <@ RunStatus.ofString "waiting" = OtherStatus "waiting" @>

// RunConclusion.ofString

[<Fact>]
let ``RunConclusion.ofString success`` () =
    test <@ RunConclusion.ofString "success" = SuccessConclusion @>

[<Fact>]
let ``RunConclusion.ofString failure`` () =
    test <@ RunConclusion.ofString "failure" = FailureConclusion @>

[<Fact>]
let ``RunConclusion.ofString cancelled`` () =
    test <@ RunConclusion.ofString "cancelled" = CancelledConclusion @>

[<Fact>]
let ``RunConclusion.ofString pending`` () =
    test <@ RunConclusion.ofString "pending" = PendingConclusion @>

[<Fact>]
let ``RunConclusion.ofString empty string`` () =
    test <@ RunConclusion.ofString "" = PendingConclusion @>

[<Fact>]
let ``RunConclusion.ofString unknown value`` () =
    test <@ RunConclusion.ofString "skipped" = OtherConclusion "skipped" @>

// hasCoverageRatchet

[<Fact>]
let ``hasCoverageRatchet - returns true when tool is listed`` () =
    let run =
        fakeRun [ ("dotnet", "tool list", Success "coverageratchet    0.8.0-alpha.4    coverageratchet") ]

    test <@ hasCoverageRatchet run = true @>

[<Fact>]
let ``hasCoverageRatchet - returns false when tool is not listed`` () =
    let run =
        fakeRun [ ("dotnet", "tool list", Success "fantomas    7.0.0    fantomas") ]

    test <@ hasCoverageRatchet run = false @>

[<Fact>]
let ``hasCoverageRatchet - returns false when command fails`` () =
    let run = fakeRun [ ("dotnet", "tool list", Failure "dotnet not found") ]
    test <@ hasCoverageRatchet run = false @>

// getCiStatus - NoRuns with dirty working copy does NOT fall back to parent

[<Fact>]
let ``getCiStatus - NoRuns with dirty working copy does not fall back to parent`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]")
              ("jj", "status", Success "Working copy changes:\nM src/Foo.fs") ]

    test <@ getCiStatus run = NoRuns @>

// getCurrentCommitSha - jj returns empty string

[<Fact>]
let ``getCurrentCommitSha - jj returns empty string falls back to git`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "")
              ("git", "rev-parse HEAD", Success "def456abc") ]

    test <@ getCurrentCommitSha run = Some "def456abc" @>

[<Fact>]
let ``getCurrentCommitSha - git returns empty string returns None`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "no jj")
              ("git", "rev-parse HEAD", Success "") ]

    test <@ getCurrentCommitSha run = None @>

// checkCiStatusForSha with queued run

[<Fact>]
let ``checkCiStatusForSha - queued run returns InProgress`` () =
    let json =
        """[{"status":"queued","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

// pushTags

[<Fact>]
let ``pushTags - exports and pushes each tag separately`` () =
    let mutable calls: (string * string) list = []

    let run (cmd: string) (args: string) =
        calls <- calls @ [ (cmd, args) ]

        match cmd, args with
        | "jj", "git export" -> Success ""
        | "git", a when a.StartsWith("push origin") -> Success ""
        | _ -> Failure "unexpected"

    pushTags run [ "v1.0.0"; "v2.0.0" ]
    test <@ calls |> List.exists (fun (c, a) -> c = "jj" && a = "git export") @>
    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a = "push origin v1.0.0") @>
    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a = "push origin v2.0.0") @>

// tagExists - jj success but tag not in output

[<Fact>]
let ``tagExists - jj success but tag not in output returns false`` () =
    let run = fakeRun [ ("jj", "tag list v1.0.0", Success "v2.0.0\nv3.0.0") ]
    test <@ tagExists run "v1.0.0" = false @>

// tagExists - both jj and git fail

[<Fact>]
let ``tagExists - both jj and git fail returns false`` () =
    let run =
        fakeRun
            [ ("jj", "tag list v1.0.0", Failure "no jj")
              ("git", "tag -l v1.0.0", Failure "no git") ]

    test <@ tagExists run "v1.0.0" = false @>

// getCiStatus - NoRuns with clean copy but parent sha empty

[<Fact>]
let ``getCiStatus - NoRuns clean copy but parent sha empty returns NoRuns`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]")
              ("jj", "status", Success "The working copy is clean")
              ("jj", "log -r @- --no-graph -T commit_id", Success "") ]

    test <@ getCiStatus run = NoRuns @>

// getCiStatus - NoRuns clean copy but parent log fails

[<Fact>]
let ``getCiStatus - NoRuns clean copy but parent log fails returns NoRuns`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]")
              ("jj", "status", Success "The working copy is clean")
              ("jj", "log -r @- --no-graph -T commit_id", Failure "no parent") ]

    test <@ getCiStatus run = NoRuns @>

// getCiStatus - non-NoRuns status returned directly without parent check

[<Fact>]
let ``getCiStatus - Failed status returned directly`` () =
    let json =
        """[{"status":"completed","conclusion":"failure","name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success json) ]

    test
        <@
            match getCiStatus run with
            | Failed _ -> true
            | _ -> false
        @>

// getCiStatus - Unknown status returned directly

[<Fact>]
let ``getCiStatus - Unknown from gh failure returned directly`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Failure "gh error") ]

    test <@ getCiStatus run = Unknown @>

// runOrFail

[<Fact>]
let ``runOrFail - success returns output`` () =
    let run = fakeRun [ ("echo", "hello", Success "hello world") ]
    test <@ runOrFail run "echo" "hello" = "hello world" @>

[<Fact>]
let ``runOrFail - failure throws exception`` () =
    let run = fakeRun [ ("bad", "cmd", Failure "it failed") ]
    Assert.ThrowsAny<exn>(fun () -> runOrFail run "bad" "cmd" |> ignore) |> ignore

// parseCiRuns - queued status

[<Fact>]
let ``parseCiRuns - parses queued status`` () =
    let json =
        """[{"status":"queued","conclusion":"","name":"CI","url":"https://example.com/1"}]"""

    let runs = parseCiRuns json
    test <@ runs.[0].Status = Queued @>
    test <@ runs.[0].Conclusion = PendingConclusion @>

// parseCiRuns - other/unknown status

[<Fact>]
let ``parseCiRuns - parses unknown status and conclusion`` () =
    let json =
        """[{"status":"waiting","conclusion":"skipped","name":"CI","url":"https://example.com/1"}]"""

    let runs = parseCiRuns json
    test <@ runs.[0].Status = OtherStatus "waiting" @>
    test <@ runs.[0].Conclusion = OtherConclusion "skipped" @>

// checkCiStatusForSha - mix of completed success and OtherStatus

[<Fact>]
let ``checkCiStatusForSha - completed success with OtherConclusion returns InProgress`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"},{"status":"completed","conclusion":"neutral","name":"Lint","url":"https://example.com/2"}]"""

    let run = fakeRun [ ("gh", ghCiArgs "abc123", Success json) ]
    let result = checkCiStatusForSha run "abc123"

    test
        <@
            match result with
            | InProgress _ -> true
            | _ -> false
        @>

// getLatestTag - prefix filtering

[<Fact>]
let ``getLatestTag - with custom prefix filters correctly`` () =
    let run =
        fakeRun [ ("jj", jjTagListArgs "mylib-v", Success "mylib-v1.0.0\nmylib-v2.0.0") ]

    test <@ getLatestTag run "mylib-v" = Some "mylib-v2.0.0" @>

[<Fact>]
let ``getLatestTag - prerelease tags sorted correctly`` () =
    let run =
        fakeRun [ ("jj", jjTagListArgs "v", Success "v1.0.0-alpha.1\nv1.0.0-alpha.2\nv1.0.0-beta.1\nv1.0.0\nv0.9.0") ]

    test <@ getLatestTag run "v" = Some "v1.0.0" @>

// getCurrentCommitSha - jj returns whitespace-only

[<Fact>]
let ``getCurrentCommitSha - jj returns whitespace only falls back to git`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "   ")
              ("git", "rev-parse HEAD", Success "abc123") ]

    test <@ getCurrentCommitSha run = Some "abc123" @>

// getCiStatus - NoRuns with "has no changes" message

[<Fact>]
let ``getCiStatus - NoRuns with no changes message falls back to parent`` () =
    let json =
        """[{"status":"completed","conclusion":"success","name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success "[]")
              ("jj", "status", Success "The working copy has no changes")
              ("jj", "log -r @- --no-graph -T commit_id", Success "def456")
              ("gh", ghCiArgs "def456", Success json) ]

    test <@ getCiStatus run = Passed @>

// isCiPassing edge cases

[<Fact>]
let ``isCiPassing - returns false for InProgress`` () =
    let json =
        """[{"status":"in_progress","conclusion":null,"name":"CI","url":"https://example.com/1"}]"""

    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Success "abc123")
              ("gh", ghCiArgs "abc123", Success json) ]

    test <@ isCiPassing run = false @>

[<Fact>]
let ``isCiPassing - returns false for Unknown`` () =
    let run =
        fakeRun
            [ ("jj", "log -r @ --no-graph -T commit_id", Failure "no jj")
              ("git", "rev-parse HEAD", Failure "no git") ]

    test <@ isCiPassing run = false @>

// tagExists - jj fails, git tag doesn't match

[<Fact>]
let ``tagExists - jj fails git returns different tag returns false`` () =
    let run =
        fakeRun
            [ ("jj", "tag list v1.0.0", Failure "no jj")
              ("git", "tag -l v1.0.0", Success "v1.0.0-beta") ]

    test <@ tagExists run "v1.0.0" = false @>

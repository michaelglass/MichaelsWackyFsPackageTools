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

[<Fact>]
let ``getLatestTag - finds latest by version sort`` () =
    let run = fakeRun [ ("git", "tag -l \"v*\"", Success "v1.0.0\nv1.2.0\nv1.1.0") ]

    test <@ getLatestTag run "v" = Some "v1.2.0" @>

[<Fact>]
let ``getLatestTag - no tags returns None`` () =
    let run = fakeRun [ ("git", "tag -l \"v*\"", Success "") ]
    test <@ getLatestTag run "v" = None @>

[<Fact>]
let ``getLatestTag - git failure returns None`` () =
    let run = fakeRun [ ("git", "tag -l \"v*\"", Failure "not a git repo") ]
    test <@ getLatestTag run "v" = None @>

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
    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a = "push origin v1.0.0") @>
    test <@ calls |> List.exists (fun (c, a) -> c = "git" && a = "push origin v2.0.0") @>

/// Canary settings for release tests that must never reach a consumer.
module FsSemanticTagger.Tests.CanaryFixtures

open System.IO
open FsSemanticTagger

/// No machine-local canary config, and a host that must never be reached. Every
/// path is under `root`, which the caller owns: a path directly in the system temp
/// dir would be shared with a second copy of the suite running at the same time.
let noCanaryUnder (root: string) : ConsumerCanary.Settings =
    {
        ConfigPath = Path.Combine(root, "no-such-fssemantictagger.json")
        Skip = false
        LogDir = Path.Combine(root, "fssemantictagger-canary-logs")
        ScratchDir = Path.Combine(root, "fssemantictagger-canary-seed")
        PackagesCache = Path.Combine(root, "fssemantictagger-canary-cache")
        VerdictDir = Path.Combine(root, "fssemantictagger-canary-verdicts")
        Ops =
            {
                RunIn = fun _ cmd _ -> failwithf "unexpected canary process: %s" cmd
                RunGate = fun _ command _ _ -> failwithf "unexpected canary gate: %s" command
            }
    }

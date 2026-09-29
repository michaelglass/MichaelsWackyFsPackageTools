/// Builders for grammar values, shared by the grammar diff and release tests.
module FsSemanticTagger.Tests.GrammarBuilders

open FsSemanticTagger

/// A single-occurrence flag with no short alias and no env binding.
let flag (long: string) (arity: FlagArity) : FlagSpec =
    {
        LongName = long
        ShortName = None
        Arity = arity
        TypeName = "bool"
        IsRepeatable = false
        Env = None
    }

let withEnv (env: EnvBinding option) (f: FlagSpec) : FlagSpec = { f with Env = env }

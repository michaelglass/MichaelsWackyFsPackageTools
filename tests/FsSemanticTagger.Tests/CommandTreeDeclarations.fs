// CommandTree's root-union declarations, as CommandTree main (unreleased, 0.13)
// defines them. The grammar reader finds attributes by full name, so these
// stand-ins exercise it exactly as the real ones will. Delete this file when the
// tests move to a CommandTree release that ships them: the names would clash.
namespace CommandTree

open System

/// Declares the env var prefix on a root command union.
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type CmdEnvPrefixAttribute(prefix: string) =
    inherit Attribute()
    member val Prefix: string = prefix

/// Declares the global-flag union of a root command union.
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type CmdGlobalsAttribute(globalsType: Type) =
    inherit Attribute()
    member val GlobalsType: Type = globalsType

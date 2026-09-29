namespace FsSemanticTagger

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions
open FsSemanticTagger.Api

// ===========================================================================
// CommandTree grammar-aware versioning.
//
// A CommandTree consumer's *realized command grammar* (the parse contract of its
// CLI: command names, positional args, flags and their arities) is not visible to
// the assembly-signature API diff: a `[<Cmd(Name = "diff-api")>]` rename or a flag
// arity change keeps the DU's type signature byte-identical yet breaks every old
// invocation. This module recovers that grammar structurally from a built assembly
// (mirroring CommandTree's own `buildUnionTree`) and diffs two versions of it, so a
// grammar break folds into the version bump even when the API surface is unchanged.
//
// The grammar is recovered ONLY via `GetCustomAttributesData()` + `Type` shape,
// never `GetCustomAttributes()` / `FSharpType.*` / `CommandReflection.fromUnion`,
// all of which throw under `MetadataLoadContext` (the diff loads assemblies whose
// dependencies may not be loadable into the running runtime).
// ===========================================================================

/// A flag's value arity — mirrors CommandTree.FlagArity but is owned here so the
/// diff never depends on the consumer's CommandTree version.
type FlagArity =
    | Nullary // --verbose (no value)
    | RequiredValue // --conf <v>  (space or inline)
    | OptionalValue // --wait[=<v>] (inline-only, bare => None)

/// A positional argument in the realized grammar.
type ArgSpec =
    {
        Name: string
        IsOptional: bool
        IsList: bool
        TypeName: string
    }

/// The environment variable a flag also reads when the command line does not
/// set it. CommandTree names it `[<CmdEnvRaw(name)>]` when present, else
/// `<prefix>_<suffix>`: the prefix is the string the consumer passes to a
/// `CommandReflection.*WithEnv` / `*AndEnv` entry point, the suffix is
/// `[<CmdEnv(suffix)>]` or the case name in SCREAMING_SNAKE_CASE.
type EnvBinding =
    /// The flag reads exactly this variable.
    | EnvVar of name: string
    /// The flag reads `<prefix>_<suffix>`, but the prefix is not a string literal
    /// at the call site (a computed value, or different literals at different
    /// call sites), so only the suffix is known.
    | EnvVarUnknownPrefix of suffix: string

/// A named flag in the realized grammar. `IsRepeatable` mirrors
/// `[<CmdFlag(Repeatable = true)>]`: the flag may occur more than once, each
/// occurrence appending a value (a non-repeatable flag rejects a duplicate).
/// `Env` is the environment variable bound to the flag, if any.
type FlagSpec =
    {
        LongName: string
        ShortName: string option
        Arity: FlagArity
        TypeName: string
        IsRepeatable: bool
        Env: EnvBinding option
    }

/// One node of the realized command tree. Descriptions/examples are intentionally
/// NOT modelled: they are cosmetic and must not influence the bump.
type CommandNode =
    | Leaf of name: string * args: ArgSpec list * flags: FlagSpec list
    | Group of name: string * children: CommandNode list

/// A consumer's whole CLI contract: the forest of top-level commands, and the
/// global flags accepted before or after any of them (the `'Globals` union passed
/// to `CommandReflection.fromUnionWithGlobals<'Cmd, 'Globals>`; empty without one).
type Grammar =
    {
        Roots: CommandNode list
        GlobalFlags: FlagSpec list
    }

/// The verdict, folded into the existing Api.ApiChange by taking the stronger bump.
type GrammarChange =
    | GNoChange
    | GAddition
    | GBreaking

module Grammar =

    // -----------------------------------------------------------------------
    // Pure diff: a change is BREAKING iff some previously-valid CLI invocation is
    // no longer valid or changes meaning; ADDITION iff new invocations become
    // valid while every old one still parses identically; NOCHANGE iff the machine
    // parse-contract is byte-identical.
    // -----------------------------------------------------------------------

    /// Stronger-wins fold over grammar verdicts: Breaking > Addition > NoChange.
    let combine (a: GrammarChange) (b: GrammarChange) : GrammarChange =
        let rank =
            function
            | GBreaking -> 2
            | GAddition -> 1
            | GNoChange -> 0

        if rank a >= rank b then a else b

    let private combineAll (changes: GrammarChange list) : GrammarChange = List.fold combine GNoChange changes

    /// Diff one matched positional argument (same index). A type change or an
    /// optional→required / list→scalar tightening breaks old invocations; a
    /// required→optional / scalar→list relaxation is additive.
    let private compareArg (prev: ArgSpec) (curr: ArgSpec) : GrammarChange =
        if prev.TypeName <> curr.TypeName then GBreaking
        elif prev.IsOptional && not curr.IsOptional then GBreaking
        elif not prev.IsOptional && curr.IsOptional then GAddition
        elif prev.IsList && not curr.IsList then GBreaking
        elif not prev.IsList && curr.IsList then GAddition
        else GNoChange

    /// Diff a leaf's positional argument lists. Positional args are matched by
    /// index (their *name* is help-only, so renaming a positional never bumps).
    /// A newly-appended arg is additive only if it is optional/list; a removed
    /// positional breaks any old invocation that supplied it.
    let private compareArgs (prev: ArgSpec list) (curr: ArgSpec list) : GrammarChange =
        let prevArr = List.toArray prev
        let currArr = List.toArray curr
        let common = min prevArr.Length currArr.Length

        let perIndex = [ for i in 0 .. common - 1 -> compareArg prevArr.[i] currArr.[i] ]

        let addedTail =
            [
                for i in common .. currArr.Length - 1 ->
                    let a = currArr.[i]
                    if a.IsOptional || a.IsList then GAddition else GBreaking
            ]

        let removedTail =
            if prevArr.Length > currArr.Length then
                [ GBreaking ]
            else
                []

        combineAll (perIndex @ addedTail @ removedTail)

    /// Could two env bindings name the same variable? An unknown prefix hides
    /// everything but the suffix, so a binding with an unknown prefix matches any
    /// name ending in `_<suffix>`: whether the prefix itself changed cannot be read.
    let private sameEnvVar (prev: EnvBinding) (curr: EnvBinding) : bool =
        match prev, curr with
        | EnvVar a, EnvVar b -> a = b
        | EnvVarUnknownPrefix a, EnvVarUnknownPrefix b -> a = b
        | EnvVar name, EnvVarUnknownPrefix suffix
        | EnvVarUnknownPrefix suffix, EnvVar name -> name.EndsWith("_" + suffix, StringComparison.Ordinal)

    /// Diff one flag's env binding. A new binding is additive (a variable that did
    /// nothing now sets the flag); a removed or renamed one breaks every caller
    /// who sets the flag through the old variable.
    let private compareEnv (prev: EnvBinding option) (curr: EnvBinding option) : GrammarChange =
        match prev, curr with
        | None, None -> GNoChange
        | None, Some _ -> GAddition
        | Some _, None -> GBreaking
        | Some p, Some c -> if sameEnvVar p c then GNoChange else GBreaking

    /// Diff one matched flag (same long name). Any arity or value-type change is
    /// breaking (a flag that used to consume its next token, or take an inline-only
    /// value, changes the meaning of old invocations). A dropped/changed short
    /// alias breaks `-x` callers; a newly-added short alias is additive. Making a
    /// flag repeatable is additive (a repeated flag, previously refused, now
    /// parses); making it single-occurrence breaks callers who repeated it. The
    /// env binding is diffed alongside (`compareEnv`), stronger verdict wins.
    let private compareFlag (prev: FlagSpec) (curr: FlagSpec) : GrammarChange =
        let commandLine =
            if prev.Arity <> curr.Arity then
                GBreaking
            elif prev.TypeName <> curr.TypeName then
                GBreaking
            elif prev.IsRepeatable && not curr.IsRepeatable then
                GBreaking
            elif prev.ShortName <> curr.ShortName then
                match prev.ShortName, curr.ShortName with
                | None, Some _ -> GAddition
                | _ -> GBreaking
            elif not prev.IsRepeatable && curr.IsRepeatable then
                GAddition
            else
                GNoChange

        combine commandLine (compareEnv prev.Env curr.Env)

    /// Diff a leaf's flags. Flags are matched by long name: a removed flag breaks
    /// `--flag` callers, an added flag is additive, a renamed flag reads as
    /// remove+add (Breaking wins).
    let private compareFlags (prev: FlagSpec list) (curr: FlagSpec list) : GrammarChange =
        let prevMap = prev |> List.map (fun f -> f.LongName, f) |> Map.ofList
        let currMap = curr |> List.map (fun f -> f.LongName, f) |> Map.ofList
        let removed = prev |> List.exists (fun f -> not (currMap.ContainsKey f.LongName))
        let added = curr |> List.exists (fun f -> not (prevMap.ContainsKey f.LongName))

        let commonChanges =
            prev
            |> List.choose (fun p -> currMap.TryFind p.LongName |> Option.map (compareFlag p))

        combineAll
            [
                if removed then
                    GBreaking
                if added then
                    GAddition
                yield! commonChanges
            ]

    let private nodeName =
        function
        | Leaf(n, _, _) -> n
        | Group(n, _) -> n

    /// Diff two command forests. Commands are matched by name: a removed command
    /// breaks callers, an added command is additive, a rename reads as remove+add.
    let rec private compareNodeLists (prev: CommandNode list) (curr: CommandNode list) : GrammarChange =
        let prevMap = prev |> List.map (fun n -> nodeName n, n) |> Map.ofList
        let currMap = curr |> List.map (fun n -> nodeName n, n) |> Map.ofList
        let removed = prev |> List.exists (fun n -> not (currMap.ContainsKey(nodeName n)))
        let added = curr |> List.exists (fun n -> not (prevMap.ContainsKey(nodeName n)))

        let commonChanges =
            prev
            |> List.choose (fun p -> currMap.TryFind(nodeName p) |> Option.map (compareNode p))

        combineAll
            [
                if removed then
                    GBreaking
                if added then
                    GAddition
                yield! commonChanges
            ]

    and private compareNode (prev: CommandNode) (curr: CommandNode) : GrammarChange =
        match prev, curr with
        | Leaf(_, pa, pf), Leaf(_, ca, cf) -> combine (compareArgs pa ca) (compareFlags pf cf)
        | Group(_, pc), Group(_, cc) -> compareNodeLists pc cc
        // A leaf<->group restructure at the same name changes how the token parses.
        | Leaf _, Group _
        | Group _, Leaf _ -> GBreaking

    /// Diff two realized grammars into a single verdict. Global flags follow the
    /// same rules as a command's flags.
    let compare (previous: Grammar) (current: Grammar) : GrammarChange =
        combine (compareNodeLists previous.Roots current.Roots) (compareFlags previous.GlobalFlags current.GlobalFlags)

    /// Project a grammar verdict onto the existing `Api.ApiChange` so it can share
    /// the bump machinery. The carried signature is a human-readable marker (the
    /// grammar diff has no assembly-signature strings of its own).
    let toApiChange (change: GrammarChange) : ApiChange =
        match change with
        | GBreaking -> Breaking(ApiSignature "grammar: a breaking CLI grammar change was detected", [])
        | GAddition -> Addition(ApiSignature "grammar: an additive CLI grammar change was detected", [])
        | GNoChange -> NoChange

    /// Fold a grammar verdict into an API verdict, stronger bump wins. The API
    /// change is kept whenever it is at least as strong (its signature list is more
    /// informative); the grammar marker is used only when the grammar is strictly
    /// stronger than the API diff — the case this whole feature exists for.
    let foldIntoApi (api: ApiChange) (change: GrammarChange) : ApiChange =
        let apiRank =
            function
            | Breaking _ -> 2
            | Addition _ -> 1
            | NoChange -> 0

        let grammarRank =
            function
            | GBreaking -> 2
            | GAddition -> 1
            | GNoChange -> 0

        if grammarRank change > apiRank api then
            toApiChange change
        else
            api

    /// What diffing `previous` against `current` cannot see, one sentence each,
    /// for `check-api` and `release` to print beside the verdict: the flags whose
    /// env var has a prefix that is not a string literal at the call site.
    let caveats (previous: Grammar) (current: Grammar) : string list =
        let rec nodeFlags =
            function
            | Leaf(_, _, flags) -> flags
            | Group(_, children) -> List.collect nodeFlags children

        let flagsWithUnknownPrefix (grammar: Grammar) =
            grammar.GlobalFlags @ List.collect nodeFlags grammar.Roots
            |> List.choose (fun f ->
                match f.Env with
                | Some(EnvVarUnknownPrefix _) -> Some("--" + f.LongName)
                | Some(EnvVar _)
                | None -> None)

        match
            flagsWithUnknownPrefix previous @ flagsWithUnknownPrefix current
            |> List.distinct
        with
        | [] -> []
        | flags ->
            [
                sprintf
                    "the CLI's env-var prefix is not a string literal where it is passed to CommandTree, so the env vars of %s are compared by suffix only; a change to the prefix is not detected"
                    (String.concat ", " flags)
            ]

    // -----------------------------------------------------------------------
    // Structural recovery under MetadataLoadContext.
    // -----------------------------------------------------------------------

    [<Literal>]
    let private compilationMappingAttr =
        "Microsoft.FSharp.Core.CompilationMappingAttribute"

    [<Literal>]
    let private optionTypeDef = "Microsoft.FSharp.Core.FSharpOption`1"

    [<Literal>]
    let private listTypeDef = "Microsoft.FSharp.Collections.FSharpList`1"

    [<Literal>]
    let private commandTreeNamespace = "CommandTree"

    /// Attribute names that mark a *command* case (vs a flag case). A union bearing
    /// any of these on a case is a command union (root or nested group).
    let private commandAttrNames =
        set
            [
                "CommandTree.CmdAttribute"
                "CommandTree.CmdArgAttribute"
                "CommandTree.CmdExampleAttribute"
                "CommandTree.CmdDefaultAttribute"
            ]

    let private genericDefName (t: Type) =
        if t.IsGenericType then
            t.GetGenericTypeDefinition().FullName
        else
            null

    let private isOptionType (t: Type) = genericDefName t = optionTypeDef
    let private isListType (t: Type) = genericDefName t = listTypeDef
    let private listElementType (t: Type) = t.GetGenericArguments().[0]

    /// The `SourceConstructFlags` value carried by an F# type's
    /// CompilationMappingAttribute, if any (1 = SumType/union, 2 = RecordType).
    let private compilationFlag (t: Type) : int option =
        t.GetCustomAttributesData()
        |> Seq.tryPick (fun a ->
            if a.AttributeType.FullName = compilationMappingAttr then
                a.ConstructorArguments
                |> Seq.tryPick (fun ca ->
                    if ca.ArgumentType.Name = "SourceConstructFlags" then
                        Some(Convert.ToInt32 ca.Value)
                    else
                        None)
            else
                None)

    // Option and list are themselves F# unions (SumType); exclude them so
    // isUnionType matches CommandReflection.isUnionType exactly.
    let private isUnionType (t: Type) =
        compilationFlag t = Some 1 && not (isOptionType t) && not (isListType t)

    let private isRecordType (t: Type) = compilationFlag t = Some 2

    /// A trailing `SomeUnion list` field — parsed by CommandTree as named `--flags`.
    let private isFlagDUList (t: Type) =
        isListType t && isUnionType (listElementType t)

    /// Convert PascalCase to kebab-case, byte-identical to CommandReflection.toKebabCase.
    let private toKebabCase (s: string) =
        let withAcronymBoundaries = Regex.Replace(s, "([A-Z]+)([A-Z][a-z])", "$1-$2")
        Regex.Replace(withAcronymBoundaries, "([a-z])([A-Z])", "$1-$2").ToLowerInvariant()

    /// Display type name for an argument/flag value, mirroring
    /// CommandReflection.getTypeName (option unwraps, list appends " list").
    let rec private getTypeName (t: Type) : string =
        match t.FullName with
        | "System.String" -> "string"
        | "System.Int32" -> "int"
        | "System.Int64" -> "int64"
        | "System.Boolean" -> "bool"
        | "System.Double" -> "float"
        | "System.Decimal" -> "decimal"
        | "System.Guid" -> "guid"
        | _ ->
            if isOptionType t then
                getTypeName (listOrOptionInner t)
            elif isListType t then
                getTypeName (listOrOptionInner t) + " list"
            else
                t.Name.ToLowerInvariant()

    and private listOrOptionInner (t: Type) = t.GetGenericArguments().[0]

    let private declaredStatic =
        BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly

    let private declaredInstance =
        BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly

    /// The declared static factory members of a union: `New<Case>` (cases with
    /// fields) and `get_<Case>` singleton getters (nullary cases). Used to recover
    /// case names for a single-case union, which F# compiles flattened (no `Tags`).
    let private caseNameFromFactory (m: MethodInfo) : string option =
        if m.Name.StartsWith("New", StringComparison.Ordinal) then
            Some(m.Name.Substring 3)
        elif
            m.Name.StartsWith("get_", StringComparison.Ordinal)
            && m.ReturnType = m.DeclaringType
        then
            Some(m.Name.Substring 4)
        else
            None

    /// Ordered case names of a union. Multi-case unions expose a nested `Tags` type
    /// whose literal int constants give the authoritative declaration order. A
    /// single-case union is compiled flattened (no `Tags`, no nested case type); its
    /// sole case is recovered from its `New<Case>`/`get_<Case>` factory.
    let private orderedCaseNames (union: Type) : string list =
        match
            union.GetNestedType("Tags", BindingFlags.Public ||| BindingFlags.NonPublic)
            |> Option.ofObj
        with
        | Some tags ->
            tags.GetFields(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
            |> Array.filter (fun f -> f.IsLiteral)
            |> Array.sortBy (fun f -> Convert.ToInt32(f.GetRawConstantValue()))
            |> Array.map (fun f -> f.Name)
            |> Array.toList
        | None ->
            union.GetMethods(declaredStatic)
            |> Array.choose caseNameFromFactory
            |> Array.distinct
            |> Array.toList

    /// A case's fields as (name, type) in declaration order. Multi-case unions keep
    /// each case's fields on the nested case type `<Union>+<Case>`; a single-case
    /// union keeps them directly on the union type (alongside the `Tag` accessor).
    /// Nullary cases have no fields. (New-method parameters are `_`-prefixed / `item`,
    /// so the instance properties are the faithful field-name source.)
    let private caseFields (union: Type) (caseName: string) : (string * Type) list =
        let readProps (declaringType: Type) =
            declaringType.GetProperties(declaredInstance)
            |> Array.filter (fun p -> p.Name <> "Tag")
            |> Array.sortBy (fun p -> p.MetadataToken)
            |> Array.map (fun p -> p.Name, p.PropertyType)
            |> Array.toList

        match
            union.GetNestedType(caseName, BindingFlags.Public ||| BindingFlags.NonPublic)
            |> Option.ofObj
        with
        | Some caseType -> readProps caseType
        | None ->
            // Single-case union (no nested case type): fields live on the union
            // itself. A nullary single-case union has none. Multi-case nullary cases
            // reach here too (no nested type) and correctly yield [] — the union's
            // own instance props are only `Tag`/`Is<Case>` for those, all filtered.
            if
                union.GetNestedType("Tags", BindingFlags.Public ||| BindingFlags.NonPublic)
                |> isNull
            then
                readProps union
                |> List.filter (fun (name, t) ->
                    not (name.StartsWith("Is", StringComparison.Ordinal) && t.FullName = "System.Boolean"))
            else
                []

    /// The custom-attribute data attached to a case: on the `New<Case>` factory
    /// method (cases with fields) or the `get_<Case>` singleton getter (nullary
    /// cases) — the members F#'s reflection associates the case attributes with.
    let private caseAttributes (union: Type) (caseName: string) : CustomAttributeData list =
        union.GetMethod("New" + caseName, declaredStatic)
        |> Option.ofObj
        |> Option.orElseWith (fun () -> union.GetMethod("get_" + caseName, declaredStatic) |> Option.ofObj)
        |> Option.map (fun m -> m.GetCustomAttributesData() |> List.ofSeq)
        |> Option.defaultValue []

    /// Value of a string-typed named argument on a specific attribute, e.g. the
    /// `Name` of `[<Cmd(Name = "diff-api")>]` or `Short` of `[<CmdFlag(Short = "k")>]`.
    let private namedString
        (attrFullName: string)
        (memberName: string)
        (attrs: CustomAttributeData list)
        : string option =
        attrs
        |> List.tryFind (fun a -> a.AttributeType.FullName = attrFullName)
        |> Option.bind (fun a ->
            a.NamedArguments
            |> Seq.tryPick (fun n ->
                if n.MemberName = memberName then
                    match n.TypedValue.Value with
                    | null -> None
                    | v -> Some(string v)
                else
                    None))

    /// Value of a bool-typed named argument on a specific attribute, e.g. the
    /// `Repeatable` of `[<CmdFlag(Repeatable = true)>]`; `false` when absent.
    let private namedBool (attrFullName: string) (memberName: string) (attrs: CustomAttributeData list) : bool =
        attrs
        |> List.tryFind (fun a -> a.AttributeType.FullName = attrFullName)
        |> Option.exists (fun a ->
            a.NamedArguments
            |> Seq.exists (fun n -> n.MemberName = memberName && n.TypedValue.Value = box true))

    /// Command name: `[<Cmd(Name = ...)>]` override, else kebab-case of the case name.
    let private commandName (caseName: string) (attrs: CustomAttributeData list) : string =
        namedString "CommandTree.CmdAttribute" "Name" attrs
        |> Option.defaultValue (toKebabCase caseName)

    /// Positional args from case fields — arg names are help-only (kebab of the
    /// field name), so `[<CmdArg>]` descriptions are irrelevant to the grammar. A
    /// list field is optional as well as a list: CommandTree (0.9+) binds zero
    /// trailing values to the empty list.
    let private argInfos (fields: (string * Type) list) : ArgSpec list =
        fields
        |> List.map (fun (name, t) ->
            {
                Name = toKebabCase name
                IsOptional = isOptionType t || isListType t
                IsList = isListType t
                TypeName = getTypeName t
            })

    /// Record-typed argument: expand the record's fields as positional args, matching
    /// CommandReflection (a `bool` field is treated as optional, like an option).
    let private recordArgInfos (recordType: Type) : ArgSpec list =
        recordType.GetProperties(declaredInstance)
        |> Array.sortBy (fun p -> p.MetadataToken)
        |> Array.map (fun p ->
            {
                Name = toKebabCase p.Name
                IsOptional = isOptionType p.PropertyType || p.PropertyType.FullName = "System.Boolean"
                IsList = false
                TypeName = getTypeName p.PropertyType
            })
        |> Array.toList

    /// Convert PascalCase to SCREAMING_SNAKE_CASE, byte-identical to
    /// CommandReflection.toScreamingSnakeCase (`LogLevel` -> `LOG_LEVEL`).
    let private toScreamingSnakeCase (s: string) =
        Regex.Replace(s, "([a-z])([A-Z])", "$1_$2").ToUpperInvariant()

    /// The string constructor argument of an attribute, e.g. the `"LVL"` of
    /// `[<CmdEnv("LVL")>]`.
    let private ctorString (attrFullName: string) (attrs: CustomAttributeData list) : string option =
        attrs
        |> List.tryFind (fun a -> a.AttributeType.FullName = attrFullName && a.ConstructorArguments.Count = 1)
        |> Option.bind (fun a ->
            match a.ConstructorArguments.[0].Value with
            | :? string as v -> Some v
            | _ -> None)

    /// The env-var prefix a grammar's flags are bound under: the string the
    /// consumer passes to a `CommandReflection.*WithEnv` / `*AndEnv` entry point.
    type private EnvPrefix =
        /// No `*Env` entry point is called with this root.
        | NoPrefix
        | LiteralPrefix of string
        /// Called, but the prefix is not one string literal.
        | UnknownPrefix

    /// The env binding of one flag case, mirroring CommandReflection.deriveEnvVar:
    /// `[<CmdEnvRaw(name)>]` wins and ignores the prefix; otherwise, only under a
    /// prefix, `<prefix>_<suffix>` with `[<CmdEnv(suffix)>]` or the case name.
    let private envBinding (prefix: EnvPrefix) (caseName: string) (attrs: CustomAttributeData list) =
        match ctorString "CommandTree.CmdEnvRawAttribute" attrs with
        | Some raw -> Some(EnvVar raw)
        | None ->
            let suffix =
                ctorString "CommandTree.CmdEnvAttribute" attrs
                |> Option.defaultWith (fun () -> toScreamingSnakeCase caseName)

            match prefix with
            | NoPrefix -> None
            | LiteralPrefix p -> Some(EnvVar(p + "_" + suffix))
            | UnknownPrefix -> Some(EnvVarUnknownPrefix suffix)

    /// Flags from a flag-DU type, mirroring CommandReflection.getFlagInfoFromDU:
    /// arity from field shape, long name from `[<CmdFlag(Name)>]` or kebab, and the
    /// same short-flag derivation (first letter, suppressed on collision unless an
    /// explicit short is given). `envOf` gives each case's env binding.
    let private flagInfos
        (envOf: string -> CustomAttributeData list -> EnvBinding option)
        (flagDUType: Type)
        : FlagSpec list =
        let data =
            orderedCaseNames flagDUType
            |> List.map (fun caseName ->
                let fields = caseFields flagDUType caseName
                let attrs = caseAttributes flagDUType caseName

                let arity =
                    match fields with
                    | [] -> Nullary
                    | (_, t) :: _ when isOptionType t -> OptionalValue
                    | _ -> RequiredValue

                let longName =
                    namedString "CommandTree.CmdFlagAttribute" "Name" attrs
                    |> Option.defaultValue (toKebabCase caseName)

                let explicitShort = namedString "CommandTree.CmdFlagAttribute" "Short" attrs
                let isRepeatable = namedBool "CommandTree.CmdFlagAttribute" "Repeatable" attrs

                let typeName =
                    match fields with
                    | [] -> "bool"
                    | (_, t) :: _ -> getTypeName t

                longName, explicitShort, arity, typeName, isRepeatable, envOf caseName attrs)

        let autoShortCounts =
            data
            |> List.choose (fun (longName, explicitShort, _, _, _, _) ->
                match explicitShort with
                | Some _ -> None
                | None -> Some(string longName.[0]))
            |> List.countBy id
            |> Map.ofList

        data
        |> List.map (fun (longName, explicitShort, arity, typeName, isRepeatable, env) ->
            let shortName =
                match explicitShort with
                | Some s -> Some s
                | None ->
                    let candidate = string longName.[0]

                    match Map.tryFind candidate autoShortCounts with
                    | Some 1 -> Some candidate
                    | _ -> None

            {
                LongName = longName
                ShortName = shortName
                Arity = arity
                TypeName = typeName
                IsRepeatable = isRepeatable
                Env = env
            })

    /// Walk one command union into its command forest, mirroring the branch order of
    /// CommandReflection.buildUnionTree exactly:
    ///   1. trailing `SomeDU list`      -> Leaf + flags (positional prefix + flags)
    ///   2. single nested union field   -> Group (recurse)
    ///   3. single record field         -> Leaf (record fields as args)
    ///   4. otherwise                    -> Leaf (fields as positional args)
    /// A command's flags are bound to env vars under `prefix`, and to a
    /// `[<CmdEnvRaw>]` name even without one: CommandTree resolves a command's
    /// flag env vars on every parse.
    let rec private walkUnion (prefix: EnvPrefix) (union: Type) : CommandNode list =
        orderedCaseNames union |> List.map (walkCase prefix union)

    and private walkCase (prefix: EnvPrefix) (union: Type) (caseName: string) : CommandNode =
        let attrs = caseAttributes union caseName
        let cmdName = commandName caseName attrs
        let fields = caseFields union caseName
        let fieldTypes = fields |> List.map snd

        let trailingIsFlagDUList =
            match List.tryLast fieldTypes with
            | Some t -> isFlagDUList t
            | None -> false

        if not (List.isEmpty fields) && trailingIsFlagDUList then
            let positional = fields |> List.take (fields.Length - 1)
            let flagDUType = listElementType (List.last fieldTypes)
            Leaf(cmdName, argInfos positional, flagInfos (envBinding prefix) flagDUType)
        elif fields.Length = 1 && isUnionType fieldTypes.Head then
            Group(cmdName, walkUnion prefix fieldTypes.Head)
        elif fields.Length = 1 && isRecordType fieldTypes.Head then
            Leaf(cmdName, recordArgInfos fieldTypes.Head, [])
        else
            Leaf(cmdName, argInfos fields, [])

    /// All loadable types of an assembly, tolerant of a `ReflectionTypeLoadException`
    /// (a dependency that MetadataLoadContext couldn't resolve): use whatever types
    /// did load rather than failing the whole extraction.
    let private safeGetTypes (asm: Assembly) : Type[] =
        try
            asm.GetTypes()
        with :? ReflectionTypeLoadException as ex ->
            ex.Types |> Array.filter (isNull >> not)

    let private hasCommandAttr (attrs: CustomAttributeData list) =
        attrs
        |> List.exists (fun a -> commandAttrNames.Contains a.AttributeType.FullName)

    /// A union whose cases carry command attributes (`[<Cmd>]` etc.) — a command
    /// union, either the root or a nested group.
    let private isCommandUnion (t: Type) =
        isUnionType t
        && (orderedCaseNames t |> List.exists (fun c -> hasCommandAttr (caseAttributes t c)))

    /// The union types directly referenced by `union` as a nested group (a case with
    /// a single bare-union field). Used to subtract nested groups from the root set.
    let private directChildUnions (union: Type) : Type list =
        orderedCaseNames union
        |> List.choose (fun caseName ->
            match caseFields union caseName |> List.map snd with
            | [ t ] when isUnionType t -> Some t
            | _ -> None)

    /// The single root command union of an assembly: a command union not referenced
    /// as a nested group by any other command union. `None` (never a guess) when
    /// there is no such union or the choice is ambiguous.
    let private findRootCommandUnion (types: Type[]) : Type option =
        let commandUnions = types |> Array.filter isCommandUnion |> Array.toList

        let referenced =
            commandUnions
            |> List.collect directChildUnions
            |> List.map (fun t -> t.FullName)
            |> Set.ofList

        match commandUnions |> List.filter (fun u -> not (referenced.Contains u.FullName)) with
        | [ single ] -> Some single
        | _ -> None

    /// Is this assembly a CommandTree consumer? It must reference the CommandTree
    /// assembly AND carry at least one union with a CommandTree attribute on a case.
    /// Non-consumers are left untouched (the API diff still governs their bump).
    let private isCommandTreeConsumer (asm: Assembly) (types: Type[]) : bool =
        let referencesCommandTree =
            asm.GetReferencedAssemblies()
            |> Array.exists (fun r -> r.Name = commandTreeNamespace)

        referencesCommandTree
        && (types
            |> Array.exists (fun t ->
                isUnionType t
                && (orderedCaseNames t
                    |> List.exists (fun c ->
                        caseAttributes t c
                        |> List.exists (fun a -> a.AttributeType.Namespace = commandTreeNamespace)))))

    /// Qualifies a type name with its namespace, as `Type.FullName` does.
    let private qualify (ns: string) (name: string) =
        if String.IsNullOrEmpty ns then name else ns + "." + name

    /// The `Type.FullName` of a type defined in the assembly being read.
    let rec private definitionName (reader: MetadataReader) (handle: TypeDefinitionHandle) : string =
        let definition = reader.GetTypeDefinition handle
        let name = reader.GetString definition.Name
        let declaring = definition.GetDeclaringType()

        if declaring.IsNil then
            qualify (reader.GetString definition.Namespace) name
        else
            definitionName reader declaring + "+" + name

    /// The full names of a generic instantiation's type arguments, when every
    /// one is a type this assembly defines; `None` for any other argument (a
    /// primitive, a constructed generic, a type from another assembly), none of
    /// which is a union this assembly declares.
    let private typeArgumentsDefinedHere (reader: MetadataReader) (spec: MethodSpecification) : string list option =
        let mutable blob = reader.GetBlobReader spec.Signature
        blob.ReadSignatureHeader() |> ignore
        let count = blob.ReadCompressedInteger()
        let names = Collections.Generic.List<string>()
        let mutable definedHere = true

        while definedHere && names.Count < count do
            let argument =
                if blob.ReadSignatureTypeCode() = SignatureTypeCode.TypeHandle then
                    blob.ReadTypeHandle()
                else
                    EntityHandle()

            if argument.Kind = HandleKind.TypeDefinition then
                names.Add(definitionName reader (TypeDefinitionHandle.op_Explicit argument))
            else
                definedHere <- false

        if definedHere then Some(List.ofSeq names) else None

    /// What a `CommandTree.CommandReflection` entry point takes beside the root
    /// command union: a globals union (`*WithGlobals*`, the second type argument)
    /// and an env-var prefix (`*Env`, the last value argument).
    type private EntryPoint =
        {
            TakesGlobals: bool
            TakesEnvPrefix: bool
        }

    let private entryPoints =
        Map.ofList
            [
                "fromUnionWithEnv",
                {
                    TakesGlobals = false
                    TakesEnvPrefix = true
                }
                "tryFromUnionWithEnv",
                {
                    TakesGlobals = false
                    TakesEnvPrefix = true
                }
                "fromUnionWithGlobals",
                {
                    TakesGlobals = true
                    TakesEnvPrefix = false
                }
                "tryFromUnionWithGlobals",
                {
                    TakesGlobals = true
                    TakesEnvPrefix = false
                }
                "fromUnionWithGlobalsAndEnv",
                {
                    TakesGlobals = true
                    TakesEnvPrefix = true
                }
                "tryFromUnionWithGlobalsAndEnv",
                {
                    TakesGlobals = true
                    TakesEnvPrefix = true
                }
            ]

    /// The entry point a referenced method is, if it is a method of
    /// `CommandTree.CommandReflection` listed in `entryPoints`.
    let private entryPointOf (reader: MetadataReader) (method: MemberReference) : EntryPoint option =
        if method.Parent.Kind <> HandleKind.TypeReference then
            None
        else
            let parent = reader.GetTypeReference(TypeReferenceHandle.op_Explicit method.Parent)

            if
                reader.GetString parent.Namespace + "." + reader.GetString parent.Name = "CommandTree.CommandReflection"
            then
                entryPoints.TryFind(reader.GetString method.Name)
            else
                None

    /// One instantiation of an entry point in the assembly's MethodSpec table:
    /// its type arguments (root first, then any globals union), and, for an
    /// `*Env` entry point, the prefix passed at each call of it (`None` for a
    /// call whose prefix is not a string literal).
    type private Instantiation =
        {
            EntryPoint: EntryPoint
            TypeArguments: string list
            Prefixes: string option list
        }

    /// The opcodes the prefix scan reads, and each opcode's operand type.
    [<Literal>]
    let private Nop = 0x00us

    [<Literal>]
    let private LdStr = 0x72us

    [<Literal>]
    let private TailPrefix = 0xFE14us

    [<Literal>]
    let private Call = 0x28us

    [<Literal>]
    let private CallVirt = 0x6Fus

    let private operandTypes =
        lazy
            (typeof<Emit.OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static)
             |> Array.map (fun f ->
                 let op = f.GetValue null :?> Emit.OpCode
                 uint16 op.Value, op.OperandType)
             |> dict)

    /// The prefix argument of every call to each MethodSpec row in `targets`, read
    /// from the IL of every method body: the string of the `ldstr` immediately
    /// before the call (a `nop` or `tail.` between them aside), since the prefix is
    /// the call's last argument. A call preceded by anything else passes a
    /// computed prefix, recorded as `None`. An opcode the runtime does not define
    /// ends the scan of that body, leaving its calls unread.
    let private prefixesAtCalls
        (pe: PEReader)
        (reader: MetadataReader)
        (targets: Set<int>)
        : Map<int, string option list> =
        let found = Collections.Generic.Dictionary<int, string option list>()

        for handle in reader.MethodDefinitions do
            let rva = (reader.GetMethodDefinition handle).RelativeVirtualAddress

            if rva <> 0 then
                let mutable il = pe.GetMethodBody(rva).GetILReader()
                let mutable previousString: string option = None

                while il.RemainingBytes > 0 do
                    let first = il.ReadByte()

                    let opcode =
                        if first = 0xFEuy then
                            0xFE00us ||| uint16 (il.ReadByte())
                        else
                            uint16 first

                    match opcode with
                    | LdStr ->
                        let token = il.ReadInt32()

                        previousString <-
                            Some(reader.GetUserString(MetadataTokens.UserStringHandle(token &&& 0xFFFFFF)))
                    | Call
                    | CallVirt ->
                        let token = il.ReadInt32()
                        let row = token &&& 0xFFFFFF

                        if (token >>> 24) = int TableIndex.MethodSpec && targets.Contains row then
                            found.[row] <-
                                previousString
                                :: (match found.TryGetValue row with
                                    | true, calls -> calls
                                    | _ -> [])

                        previousString <- None
                    | Nop
                    | TailPrefix -> ()
                    | _ ->
                        let size =
                            match operandTypes.Value.TryGetValue opcode with
                            | false, _ -> il.RemainingBytes
                            | true, operandType ->
                                match operandType with
                                | Emit.OperandType.InlineNone -> 0
                                | Emit.OperandType.ShortInlineBrTarget
                                | Emit.OperandType.ShortInlineI
                                | Emit.OperandType.ShortInlineVar -> 1
                                | Emit.OperandType.InlineVar -> 2
                                | Emit.OperandType.InlineI8
                                | Emit.OperandType.InlineR -> 8
                                | Emit.OperandType.InlineSwitch -> 4 * il.ReadInt32()
                                | _ -> 4

                        il.Offset <- il.Offset + size
                        previousString <- None

        found |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// Every instantiation of a CommandTree entry point whose type arguments are
    /// all types this assembly defines. The call site is the one place the
    /// program says which union holds its global flags (a globals case needs no
    /// attribute, so the union cannot be recognised by its shape) and which
    /// prefix its env vars take.
    let private instantiations (dllPath: string) : Instantiation list =
        use stream = File.OpenRead dllPath
        use pe = new PEReader(stream)
        let reader = pe.GetMetadataReader()

        let specs =
            [
                for row in 1 .. reader.GetTableRowCount TableIndex.MethodSpec do
                    let spec =
                        reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle row)

                    let entryPoint =
                        if spec.Method.Kind = HandleKind.MemberReference then
                            entryPointOf
                                reader
                                (reader.GetMemberReference(MemberReferenceHandle.op_Explicit spec.Method))
                        else
                            None

                    match
                        entryPoint
                        |> Option.bind (fun e -> typeArgumentsDefinedHere reader spec |> Option.map (fun a -> e, a))
                    with
                    | Some(e, arguments) -> yield row, e, arguments
                    | None -> ()
            ]

        let prefixes =
            specs
            |> List.filter (fun (_, e, _) -> e.TakesEnvPrefix)
            |> List.map (fun (row, _, _) -> row)
            |> Set.ofList
            |> prefixesAtCalls pe reader

        [
            for row, e, arguments in specs ->
                {
                    EntryPoint = e
                    TypeArguments = arguments
                    Prefixes = prefixes.TryFind row |> Option.defaultValue []
                }
        ]

    /// The env-var prefix `root` is parsed with. A root passed to an `*Env` entry
    /// point with one string literal at every call has that prefix. Any other
    /// prefix is unknown rather than guessed: a computed value, different literals
    /// at different calls, or an instantiation with no call to read (its method
    /// passed as a value).
    let private envPrefixOf (calls: Instantiation list) : EnvPrefix =
        match calls |> List.filter (fun i -> i.EntryPoint.TakesEnvPrefix) with
        | [] -> NoPrefix
        | envCalls ->
            let prefixes =
                envCalls
                |> List.collect (fun i -> if List.isEmpty i.Prefixes then [ None ] else i.Prefixes)
                |> List.distinct

            match prefixes with
            | [ Some prefix ] -> LiteralPrefix prefix
            | _ -> UnknownPrefix

    /// The global flags `root` is parsed with: the flags union the assembly passes
    /// alongside `root` to a `*WithGlobals` entry point. None when it never does,
    /// and none when it passes several different unions, since which one a given
    /// run uses cannot be read from metadata. Global flags read env vars only
    /// through an `*AndEnv` entry point: CommandTree skips their env resolution,
    /// `[<CmdEnvRaw>]` included, without a prefix.
    let private globalFlagsOf (asm: Assembly) (calls: Instantiation list) (prefix: EnvPrefix) : FlagSpec list =
        let withGlobals = calls |> List.filter (fun i -> i.EntryPoint.TakesGlobals)

        let envOf =
            if withGlobals |> List.exists (fun i -> i.EntryPoint.TakesEnvPrefix) then
                envBinding prefix
            else
                fun _ _ -> None

        match withGlobals |> List.map (fun i -> List.last i.TypeArguments) |> List.distinct with
        | [ globals ] ->
            match asm.GetType globals |> Option.ofObj with
            | Some t when isUnionType t -> flagInfos envOf t
            | _ -> []
        | _ -> []

    /// The realized grammar rooted at `root`: its command forest and its global flags.
    let private grammarOf (dllPath: string) (asm: Assembly) (root: Type) : Grammar =
        let calls =
            instantiations dllPath
            |> List.filter (fun i -> List.head i.TypeArguments = root.FullName)

        let prefix = envPrefixOf calls

        {
            Roots = walkUnion prefix root
            GlobalFlags = globalFlagsOf asm calls prefix
        }

    /// Recover the realized CLI grammar of a single named root command union in an
    /// assembly. Internal seam for tests: bypasses consumer detection / root
    /// discovery so a fixture DU can be walked by full name. `None` on any read
    /// failure or when the named type isn't a union.
    let internal extractGrammarForType (dllPath: string) (rootTypeFullName: string) : Grammar option =
        try
            let resolver = createResolver dllPath
            use context = new MetadataLoadContext(resolver)
            let asm = context.LoadFromAssemblyPath(Path.GetFullPath dllPath)

            match asm.GetType(rootTypeFullName) |> Option.ofObj with
            | Some t when isUnionType t -> Some(grammarOf dllPath asm t)
            | _ -> None
        with _ ->
            None

    /// Recover the realized CLI grammar of a CommandTree consumer assembly, or
    /// `None` when the assembly is not a consumer, has no unambiguous root command
    /// union, or can't be read. NEVER fabricates a grammar — the API diff still
    /// governs the bump when this yields `None`.
    let extractGrammarFromAssembly (dllPath: string) : Grammar option =
        try
            let resolver = createResolver dllPath
            use context = new MetadataLoadContext(resolver)
            let asm = context.LoadFromAssemblyPath(Path.GetFullPath dllPath)
            let types = safeGetTypes asm

            if not (isCommandTreeConsumer asm types) then
                None
            else
                findRootCommandUnion types |> Option.map (grammarOf dllPath asm)
        with _ ->
            None

    /// Grammar counterpart of `Api.extractFromCacheRoot`: recover a previously
    /// published package's grammar from an arbitrary cache root. Looks in exactly
    /// the same directories the API extractor does (`Api.packageCacheSearch`).
    let extractGrammarFromCacheRoot (cacheRoot: string) (packageId: string) (version: string) : Grammar option =
        match packageCacheSearch cacheRoot packageId version with
        | None -> None
        | Some(searchDirs, dllName) ->
            searchDirs
            |> List.tryPick (fun dir ->
                let dllPath = Path.Combine(dir, dllName)

                if File.Exists(dllPath) then
                    extractGrammarFromAssembly dllPath
                else
                    None)

    /// Grammar counterpart of `Api.extractPreviousFromNuGet`: read a prior release's
    /// grammar from the user-local NuGet cache. Cache-only by design — the release
    /// flow has already downloaded the package while extracting its API, so a miss
    /// here simply means "no grammar to diff" and the API diff governs the bump.
    let extractPreviousGrammarFromNuGet (packageId: string) (version: string) : Grammar option =
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        extractGrammarFromCacheRoot (Path.Combine(home, ".nuget", "packages")) packageId version

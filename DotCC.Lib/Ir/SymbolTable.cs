#nullable enable

using System;
using System.Collections.Generic;

namespace DotCC.Ir;

/// <summary>A source location, carried on IR nodes for diagnostics.</summary>
public readonly record struct SrcPos(int Line, int Column)
{
    /// <summary>
    /// Line numbers at or above this base belong to a synthetic system header's
    /// reserved band: the <c>#include</c> sub-lexer starts an embedded header here
    /// (via <c>BytesLexer</c>'s <c>initialLine</c>) so every prototype it declares is
    /// distinguishable by position from the user's translation unit. Far above any
    /// real source-line count (~1.05M), so a band line never collides with a user line.
    /// </summary>
    public const int SyntheticLineBase = 1 << 20;

    /// <summary>True if this position lies in the synthetic-header band — i.e. the
    /// token came from one of dotcc's embedded system headers, not user source.</summary>
    public bool IsSystemHeader => Line >= SyntheticLineBase;

    public static SrcPos From(global::LALR.CC.LexicalGrammar.Item it) =>
        new(it.Position.Line, it.Position.Column);

    /// <summary>
    /// Render a line number for a user-facing diagnostic, masking the synthetic band
    /// so a message never prints a 1048576-based number: a band line shows as
    /// <c>&lt;system header&gt;:N</c> (N = 1-based line within that header).
    /// </summary>
    public static string DescribeLine(int line) =>
        line >= SyntheticLineBase ? $"<system header>:{line - SyntheticLineBase + 1}" : $"{line}";

    public override string ToString() =>
        Line >= SyntheticLineBase ? $"<system header>:{Line - SyntheticLineBase + 1}:{Column}" : $"{Line}:{Column}";
}

/// <summary>What a <see cref="Symbol"/> names.</summary>
public enum SymKind { Var, Param, Func, EnumConst, Typedef, Field }

/// <summary>C storage class. Drives linkage + where codegen places the symbol
/// (a file-scope <c>static</c> field, a function-static global, a local).</summary>
public enum Storage { None, Auto, Static, Extern, Register, Typedef }

/// <summary>
/// A resolved name. Replaces the legacy emitter's ~dozen parallel side-tables
/// (<c>_localTypes</c>/<c>_globalTypes</c>/<c>_fnStatics</c>/<c>_localNames</c>/…)
/// with a single typed record. <see cref="TargetName"/> is the identifier the
/// backend actually prints — escaped and (where the target forbids shadowing)
/// uniquified by the injected <see cref="INameLegalizer"/> — while
/// <see cref="Name"/> stays the raw source spelling for lookups.
/// </summary>
public sealed class Symbol
{
    public required string Name { get; init; }
    public required SymKind Kind { get; init; }
    /// <summary>The resolved C type. Settable so a post-build IR pass can retype a
    /// symbol — the malloc→stack-value peephole demotes a <c>T*</c> local to the
    /// value type <c>T</c> in place, so every shared <see cref="VarRef"/> sees it.</summary>
    public required CType Type { get; set; }
    public Storage Storage { get; init; } = Storage.None;
    /// <summary>For an <see cref="SymKind.EnumConst"/> or a C23 <c>constexpr</c>
    /// object (<see cref="IsConstexpr"/>): its integer value. C enums lower to plain
    /// integer constants (dotcc emits the literal value at each use), sidestepping
    /// C#'s enum-arithmetic restrictions; a constexpr binds it after its initializer
    /// is built + folded (hence settable), so the name resolves in every ICE
    /// position via <c>ConstEval</c>.</summary>
    public long ConstValue { get; set; }
    /// <summary>The identifier the backend emits (set by
    /// <see cref="SymbolTable.Declare"/> via the target's <see cref="INameLegalizer"/>:
    /// escaped, and uniquified when the target forbids shadowing).</summary>
    public string TargetName { get; set; } = "";
    /// <summary>True for a file-scope (global) symbol — codegen emits it as a
    /// <c>DotCcGlobals</c> field rather than a block local.</summary>
    public bool IsGlobal { get; init; }

    /// <summary>True when this symbol was declared in one of dotcc's synthetic system
    /// headers (detected via the reserved line band — see <see cref="SrcPos.SyntheticLineBase"/>).
    /// A proto-only function with this set is runtime-provided (libc, surfaced by
    /// <c>using static Libc</c>), so import mode never treats it as an <c>-l</c> candidate;
    /// a user-declared proto (band-free) IS importable.</summary>
    public bool FromSystemHeader { get; init; }

    /// <summary>The target-neutral C-level fact that this object's address is taken
    /// (<c>&amp;x</c>) somewhere — set for any var or param at the single site every
    /// <c>&amp;</c> node is built (<see cref="IrBuilder"/>). A pure semantic flag; each
    /// backend decides what it implies. The C# backend stores an address-taken pointer
    /// <em>global</em> as <c>nint</c> (see <c>CSharpBackend.NintStorage</c>); the wat backend
    /// gives any address-taken local/param a linear-memory frame slot.</summary>
    public bool AddressTaken { get; set; }

    /// <summary>True when the function is declared to never return to its caller —
    /// the C11 <c>_Noreturn</c> specifier, its C23 <c>noreturn</c> spelling, or the
    /// C23 <c>[[noreturn]]</c> attribute (any declaration marks the shared symbol).
    /// The C# backend surfaces it as
    /// <c>[System.Diagnostics.CodeAnalysis.DoesNotReturn]</c> on the emitted method.</summary>
    public bool IsNoReturn { get; set; }

    /// <summary>True when the function is declared <c>inline</c> (C99; any
    /// declaration marks the shared symbol). The C# backend surfaces it as
    /// <c>[MethodImpl(MethodImplOptions.AggressiveInlining)]</c> — a real JIT
    /// hint, the faithful lowering of C's "please inline this".</summary>
    public bool IsInline { get; set; }

    /// <summary>True when the object has THREAD storage duration — C11
    /// <c>_Thread_local</c> (C23 <c>thread_local</c>) or Zig <c>threadlocal var</c>.
    /// The C# backend emits <c>[ThreadStatic]</c> on the global's field. Constraint
    /// (enforced at build time): only a zero/default initializer — a .NET
    /// [ThreadStatic] initializer would run on the first thread only, breaking C's
    /// "every thread starts at the initial value".</summary>
    public bool IsThreadLocal { get; init; }

    /// <summary>True for a C23 <c>constexpr</c> object declaration. The symbol's
    /// <see cref="Type"/> is const-qualified (writes reject via the standard
    /// const-correctness error) and <see cref="ConstValue"/> holds the folded
    /// initializer, which <c>ConstEval</c> substitutes at every use in a constant
    /// expression — the emitted C# stays an ordinary field/local (reads and
    /// <c>&amp;</c> work; runtime behavior is identical).</summary>
    public bool IsConstexpr { get; init; }

    /// <summary>Non-null when the function carries the C23 <c>[[nodiscard]]</c> /
    /// <c>[[nodiscard("reason")]]</c> attribute — the decoded reason, or <c>""</c> for
    /// the message-less form. Unlike the others this drives no C# attribute (the BCL
    /// has no must-use-result marker); instead <see cref="IrBuilder"/> emits a
    /// gcc-shaped warning when a call's non-void result is discarded in statement
    /// position (a <c>(void)</c> cast suppresses it, as in C).</summary>
    public string? Nodiscard { get; set; }

    /// <summary>Non-null when the function carries the C23 <c>[[deprecated]]</c> /
    /// <c>[[deprecated("msg")]]</c> attribute — the decoded message, or <c>""</c> for
    /// the message-less form. The C# backend surfaces it as <c>[System.Obsolete(…)]</c>,
    /// so the .NET build of the emitted program warns at call sites the way a C
    /// compiler warns on a call to a deprecated function.</summary>
    public string? Deprecated { get; set; }
}

/// <summary>
/// Lexically-scoped symbol table. One per translation unit. Owns name
/// resolution (<see cref="Resolve"/>) and the shadow alpha-rename, but only the
/// neutral MECHANISM: it tracks the identifiers used in the current function and,
/// when the injected <see cref="INameLegalizer"/> says the target forbids
/// shadowing (C# — CS0136; C does not), uniquifies a colliding local. The escape
/// rule, the shadow rule, and the uniquified spelling are all the legalizer's
/// POLICY. The rename rides only <see cref="Symbol.TargetName"/>; all lookups stay
/// keyed by the raw source name.
/// </summary>
public sealed class SymbolTable
{
    private readonly List<Dictionary<string, Symbol>> _scopes = new();
    // Identifiers already emitted in the CURRENT function — drives uniquification.
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    // The target's identifier policy (escape + shadow rule), always injected by
    // the caller — which backend's policy applies is the COMPILER's decision
    // (Compiler.BuildIr), so the neutral IR namespace never references a
    // concrete backend. The mechanism below stays target-neutral.
    private readonly INameLegalizer _names;

    /// <summary>Construct over a specific identifier policy. Internal because the
    /// policy type is a backend detail.</summary>
    internal SymbolTable(INameLegalizer names)
    {
        _names = names;
        _scopes.Add(new Dictionary<string, Symbol>(StringComparer.Ordinal)); // file scope
    }

    /// <summary>Escape a raw source name to a legal target identifier via the
    /// active policy — for a symbol the builder names itself (a function-static
    /// local's program-unique backing field) rather than through <see cref="Declare"/>.</summary>
    public string Escape(string rawName) => _names.Escape(rawName);

    public void EnterScope() => _scopes.Add(new Dictionary<string, Symbol>(StringComparer.Ordinal));
    public void ExitScope() { if (_scopes.Count > 1) { _scopes.RemoveAt(_scopes.Count - 1); } }

    /// <summary>Begin a function body: reset the per-function used-name set so
    /// renames are scoped to the function (two functions can each have a local
    /// <c>i</c>).</summary>
    public void BeginFunction() => _usedNames.Clear();

    public bool AtFileScope => _scopes.Count == 1;

    /// <summary>Declare a symbol in the innermost scope, computing its
    /// <see cref="Symbol.TargetName"/> via the target's <see cref="INameLegalizer"/>.
    /// File-scope symbols and params keep their escaped name; when the target forbids
    /// shadowing, a block local that collides with an identifier already used in the
    /// function is uniquified.</summary>
    public Symbol Declare(Symbol sym)
    {
        var escaped = _names.Escape(sym.Name);
        if (AtFileScope || sym.Kind is SymKind.Func or SymKind.Param)
        {
            sym.TargetName = escaped;
        }
        else
        {
            var emitted = escaped;
            if (_names.ForbidsShadowing && _usedNames.Contains(emitted))
            {
                var k = 1;
                while (_usedNames.Contains(emitted = _names.Uniquify(escaped, k))) { k++; }
            }
            sym.TargetName = emitted;
        }
        _usedNames.Add(sym.TargetName);
        _scopes[^1][sym.Name] = sym;
        return sym;
    }

    /// <summary>Register a symbol whose <see cref="Symbol.TargetName"/> is already
    /// set, in the innermost scope, WITHOUT recomputing it — for a function-scope
    /// <c>static</c> local, whose backing field is named (and made program-unique)
    /// by the builder, not by the per-function rename here.</summary>
    public Symbol DeclareAlias(Symbol sym)
    {
        _usedNames.Add(sym.TargetName);
        _scopes[^1][sym.Name] = sym;
        return sym;
    }

    /// <summary>Resolve a raw C name innermost → outermost, or null if unbound.</summary>
    public Symbol? Resolve(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var s)) { return s; }
        }
        return null;
    }
}

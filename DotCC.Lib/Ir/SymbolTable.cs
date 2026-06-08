#nullable enable

using System;
using System.Collections.Generic;

namespace DotCC.Ir;

/// <summary>A source location, carried on IR nodes for diagnostics.</summary>
public readonly record struct SrcPos(int Line, int Column)
{
    public static SrcPos From(global::LALR.CC.LexicalGrammar.Item it) =>
        new(it.Position.Line, it.Position.Column);

    public override string ToString() => $"{Line}:{Column}";
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
    /// <summary>For an <see cref="SymKind.EnumConst"/>: its integer value. C enums
    /// lower to plain integer constants (dotcc emits the literal value at each
    /// use), sidestepping C#'s enum-arithmetic restrictions.</summary>
    public long ConstValue { get; init; }
    /// <summary>The identifier the backend emits (set by
    /// <see cref="SymbolTable.Declare"/> via the target's <see cref="INameLegalizer"/>:
    /// escaped, and uniquified when the target forbids shadowing).</summary>
    public string TargetName { get; set; } = "";
    /// <summary>True for a file-scope (global) symbol — codegen emits it as a
    /// <c>DotCcGlobals</c> field rather than a block local.</summary>
    public bool IsGlobal { get; init; }

    /// <summary>The C-level fact that this (global) symbol's address is taken
    /// somewhere. A pure semantic flag — the backend decides what it implies. The
    /// C# backend uses it (together with <see cref="CType.IsPointerLowered"/>) to
    /// store a pointer/fn-ptr global as <c>nint</c> instead of a pointer field: C#
    /// forbids a pointer type as the <c>T</c> of <c>Unsafe.AsPointer&lt;T&gt;</c> /
    /// <c>Volatile.*&lt;T&gt;</c> (CS0306) and can't take a bare <c>&amp;</c> of a
    /// moveable static field (CS0212); a <c>nint</c> field satisfies both.</summary>
    public bool AddressTaken { get; set; }
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
    // The target's identifier policy (escape + shadow rule). Defaults to C#; a
    // second backend injects its own. The mechanism below stays target-neutral.
    private readonly INameLegalizer _names;

    public SymbolTable() : this(null) { }

    /// <summary>Construct over a specific identifier policy. Internal because the
    /// policy type is a backend detail; the public parameterless ctor defaults to C#.</summary>
    internal SymbolTable(INameLegalizer? names)
    {
        _names = names ?? new CSharpNameLegalizer();
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

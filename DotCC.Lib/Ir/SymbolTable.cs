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
/// with a single typed record. <see cref="CsName"/> is the identifier codegen
/// actually prints — alpha-renamed for CS0136 avoidance and/or <c>@</c>-escaped
/// — while <see cref="Name"/> stays the raw C spelling for lookups.
/// </summary>
public sealed class Symbol
{
    public required string Name { get; init; }
    public required SymKind Kind { get; init; }
    public required CType Type { get; init; }
    public Storage Storage { get; init; } = Storage.None;
    /// <summary>For an <see cref="SymKind.EnumConst"/>: its integer value. C enums
    /// lower to plain integer constants (dotcc emits the literal value at each
    /// use), sidestepping C#'s enum-arithmetic restrictions.</summary>
    public long ConstValue { get; init; }
    /// <summary>The emitted C# identifier (set by <see cref="SymbolTable.Declare"/>).</summary>
    public string CsName { get; set; } = "";
    /// <summary>True for a file-scope (global) symbol — codegen emits it as a
    /// <c>DotCcGlobals</c> field rather than a block local.</summary>
    public bool IsGlobal { get; init; }
}

/// <summary>
/// Lexically-scoped symbol table. One per translation unit. Owns name
/// resolution (<see cref="Resolve"/>) and the CS0136 alpha-rename: C permits a
/// nested block local to shadow an enclosing one (in either textual order), C#
/// does not, so <see cref="Declare"/> uniquifies a colliding local to
/// <c>name__k</c> against every identifier already used in the function. The
/// rename rides only <see cref="Symbol.CsName"/>; all lookups stay keyed by the
/// raw C name.
/// </summary>
public sealed class SymbolTable
{
    private readonly List<Dictionary<string, Symbol>> _scopes = new();
    // Identifiers already emitted in the CURRENT function — drives uniquification.
    private readonly HashSet<string> _usedCsNames = new(StringComparer.Ordinal);

    public SymbolTable() => _scopes.Add(new Dictionary<string, Symbol>(StringComparer.Ordinal)); // file scope

    public void EnterScope() => _scopes.Add(new Dictionary<string, Symbol>(StringComparer.Ordinal));
    public void ExitScope() { if (_scopes.Count > 1) { _scopes.RemoveAt(_scopes.Count - 1); } }

    /// <summary>Begin a function body: reset the per-function used-name set so
    /// renames are scoped to the function (two functions can each have a local
    /// <c>i</c>).</summary>
    public void BeginFunction() => _usedCsNames.Clear();

    public bool AtFileScope => _scopes.Count == 1;

    /// <summary>Declare a symbol in the innermost scope, computing its
    /// <see cref="Symbol.CsName"/>. File-scope symbols and params keep their raw
    /// (escaped) name; a block local that collides with an identifier already
    /// used in the function gets a fresh <c>name__k</c>.</summary>
    public Symbol Declare(Symbol sym)
    {
        var escaped = DotCC.CSharpEmitter.Id(sym.Name);
        if (AtFileScope || sym.Kind is SymKind.Func or SymKind.Param)
        {
            sym.CsName = escaped;
        }
        else
        {
            var emitted = escaped;
            if (_usedCsNames.Contains(emitted))
            {
                var k = 1;
                while (_usedCsNames.Contains(emitted = $"{escaped}__{k}")) { k++; }
            }
            sym.CsName = emitted;
        }
        _usedCsNames.Add(sym.CsName);
        _scopes[^1][sym.Name] = sym;
        return sym;
    }

    /// <summary>Register a symbol whose <see cref="Symbol.CsName"/> is already
    /// set, in the innermost scope, WITHOUT recomputing it — for a function-scope
    /// <c>static</c> local, whose backing field is named (and made program-unique)
    /// by the builder, not by the per-function rename here.</summary>
    public Symbol DeclareAlias(Symbol sym)
    {
        _usedCsNames.Add(sym.CsName);
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

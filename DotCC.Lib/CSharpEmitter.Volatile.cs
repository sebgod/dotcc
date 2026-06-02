#nullable enable

using System;
using System.Collections.Generic;
using LALR.CC.LexicalGrammar;

namespace DotCC;

internal sealed partial class CSharpEmitter
{
    // ---- volatile lowering ------------------------------------------------
    // A `volatile`-qualified lvalue is read/written through
    // System.Threading.Volatile instead of a plain field access — faithful to C's
    // "do not elide or reorder this access" guarantee (e.g. a flag a signal handler
    // mutates, like Lua's `volatile sig_atomic_t trap;`). The lvalue node emits the
    // READ form `Volatile.Read(ref lv)` by default and tags itself with the bare
    // lvalue text; the bounded set of write-context parents (assignment, compound
    // assignment, ++/--, &) reads the tag and emits the WRITE form instead —
    // `Volatile.Write(ref lv, …)` / the bare address. Same "tag-and-react" shape the
    // enum layer uses.
    //
    // C-vs-C# memory model: C `volatile` only forbids eliding/reordering accesses to
    // THAT object; it gives no inter-thread ordering for other objects. C#'s
    // Volatile.Read/Write add acquire/release half-fences — strictly STRONGER. That
    // is a safe over-approximation (never weaker than C requires), so it can't break
    // a correct C program; it just isn't a bit-for-bit semantic match. (`_Atomic`,
    // which DOES need ordering + atomicity, is a separate feature.)

    // C# types for which a `Volatile.Read`/`Volatile.Write` overload exists. Only
    // these get the fenced lowering; a volatile pointer / struct / enum / CBool
    // lvalue falls back to a plain (erased) access for now — pointer-to-volatile is
    // phase V2. (There is no `char` overload, but dotcc's `char` is `byte` anyway.)
    private static readonly HashSet<string> _volatileEligible = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint",
        "long", "ulong", "float", "double", "nint", "nuint",
    };

    internal static bool IsVolatileEligible(string csType) => _volatileEligible.Contains(csType);

    // Raw C names of block-scope locals / params declared volatile (eligible type
    // only). Cleared per function in StartFn, like _localTypes.
    private readonly HashSet<string> _localVolatile = new(StringComparer.Ordinal);
    // Raw C names of file-scope vars declared volatile (eligible type only).
    private readonly HashSet<string> _globalVolatile = new(StringComparer.Ordinal);
    // structType → set of volatile (eligible) field names. Populated by
    // StructMemberList into _pendingVolatileFields; drained per struct body, like
    // _structFieldEnums.
    private readonly Dictionary<string, HashSet<string>> _structVolatileFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingVolatileFields = new(StringComparer.Ordinal);

    // The volatile flag set by Visit(TypeVolatile) on a Type result.
    private static bool VolatileOf(Item typeItem) =>
        typeItem.Content is EmitContent.Text { Volatile: true };

    // The bare lvalue text of a volatile-lvalue EXPRESSION (set by the lvalue nodes),
    // or null. Write-context parents read this to emit the write form.
    private static string? VLValueOf(Item exprItem) =>
        (exprItem.Content as EmitContent.Text)?.VolatileLValue;

    // Drain pending volatile fields under each aggregate name (like DrainFieldEnums).
    private void DrainVolatileFields(params string[] typeNames)
    {
        if (_pendingVolatileFields.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structVolatileFields[tn] = new HashSet<string>(_pendingVolatileFields, StringComparer.Ordinal);
            }
            _pendingVolatileFields.Clear();
        }
    }

    // True if `rawName` resolves to a volatile (eligible) local/param/global var.
    // Locals win over globals (shadowing), matching VarCType / the enum lookup.
    private bool IsVolatileVar(string rawName) =>
        _localVolatile.Contains(rawName)
        || (!_localNames.Contains(rawName) && _globalVolatile.Contains(rawName));

    // Is `field` a volatile (eligible) field of the struct that baseItem's CType
    // names? Same base-type resolution as FieldEnum (struct value → "S", pointer →
    // "S*", peel the `*`).
    private bool FieldVolatile(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return false; }
        var t = StructKeyOf(s.CsType);
        return _structVolatileFields.TryGetValue(t, out var set) && set.Contains(field);
    }

    // The volatile READ form for a bare lvalue `lv` of eligible type — the value
    // C# fenced read returns, tagged with the bare lvalue so a write-context parent
    // can recover it. Carries through the enum tag / CType the lvalue would have.
    private static EmitContent.Text VolatileReadOf(string lv, string? enumType, CType? ty) =>
        new($"global::System.Threading.Volatile.Read(ref {lv})", enumType, ty, VolatileLValue: lv);

    // The volatile WRITE expression for `lv` ← `rhs`. Note: Volatile.Write returns
    // void, so this is valid in statement position (the overwhelmingly common case);
    // an assignment used as an rvalue with a volatile LHS isn't supported (documented).
    private static string VolatileWriteOf(string lv, string rhs) =>
        $"global::System.Threading.Volatile.Write(ref {lv}, {rhs})";

    // ---- phase V2: pointer-to-volatile ------------------------------------
    // A pointer whose POINTEE is volatile (`volatile int *p`) — `*p` and `p[i]` are
    // volatile lvalues and fence; the pointer object itself is NOT volatile. Tables
    // parallel the scalar ones; the "pointee volatile" fact rides forward as the
    // VolatilePointee flag (on the Type from typePtr, re-tagged on a pointer read by
    // Var / member access), which Deref / Subscript consult.

    // Raw C names of locals/params / file-scope vars that are pointers-to-volatile
    // (single level, eligible pointee). _localVolatilePointee is cleared per function.
    private readonly HashSet<string> _localVolatilePointee = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalVolatilePointee = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _structVolatilePointeeFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingVolatilePointeeFields = new(StringComparer.Ordinal);

    // The VolatilePointee flag on a Type result / pointer-valued expression.
    private static bool VolatilePointeeOf(Item it) =>
        (it.Content as EmitContent.Text)?.VolatilePointee == true;

    // If `ptrType` is `<eligible-scalar>*` (exactly one pointer level to a
    // Volatile-eligible scalar), return the pointee C# type; else null. Single-level
    // only — `volatile int **p` (pointee is itself a pointer) isn't fenced in V2.
    private static string? VolatilePointeeEligible(string ptrType)
    {
        var t = ptrType.TrimEnd();
        if (!t.EndsWith("*", StringComparison.Ordinal)) { return null; }
        var pointee = t[..^1].TrimEnd();
        return IsVolatileEligible(pointee) ? pointee : null;
    }

    private void DrainVolatilePointeeFields(params string[] typeNames)
    {
        if (_pendingVolatilePointeeFields.Count > 0)
        {
            foreach (var tn in typeNames)
            {
                _structVolatilePointeeFields[tn] = new HashSet<string>(_pendingVolatilePointeeFields, StringComparer.Ordinal);
            }
            _pendingVolatilePointeeFields.Clear();
        }
    }

    private bool IsVolatilePointeeVar(string rawName) =>
        _localVolatilePointee.Contains(rawName)
        || (!_localNames.Contains(rawName) && _globalVolatilePointee.Contains(rawName));

    private bool FieldVolatilePointee(Item baseItem, string field)
    {
        if (TyOf(baseItem) is not CType.Sized s) { return false; }
        var t = StructKeyOf(s.CsType);
        return _structVolatilePointeeFields.TryGetValue(t, out var set) && set.Contains(field);
    }

    // ---- volatile parameters ----------------------------------------------
    // A param `volatile int x` / `volatile int *reg` (the MMIO idiom) — params
    // bypass EmitDecl (they're staged in _pendingParams and adopted by StartFn), so
    // a Param visitor notes a volatile-eligible / pointer-to-volatile param name
    // here, and StartFn drains it into _localVolatile / _localVolatilePointee (params
    // keep their raw spelling, so the key matches). Cleared by StartFn each function.
    private readonly List<string> _pendingVolatileParams = new();
    private readonly List<string> _pendingVolatilePointeeParams = new();

    private void NoteVolatileParam(Item typeItem, string typeString, string name)
    {
        // _Atomic wins over volatile when both qualify the same param (atomic ops
        // already fence). Pointer-to-volatile is independent.
        if (AtomicOf(typeItem) && IsAtomicEligible(typeString)) { _pendingAtomicParams.Add(name); }
        else if (VolatileOf(typeItem) && IsVolatileEligible(typeString)) { _pendingVolatileParams.Add(name); }
        else if (VolatilePointeeOf(typeItem) && VolatilePointeeEligible(typeString) is not null) { _pendingVolatilePointeeParams.Add(name); }
    }
}

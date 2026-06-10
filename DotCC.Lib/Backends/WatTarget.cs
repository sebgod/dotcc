#nullable enable

namespace DotCC.Backends;

using DotCC.Ir;

/// <summary>
/// The WebAssembly-text backend's type projection — the first second consumer of
/// <see cref="ITarget"/>, and the one that proves the seam generalises past C#.
/// WebAssembly has only four value types, so C's whole integer zoo collapses by
/// width onto <c>i32</c>/<c>i64</c> (LP64: <c>long</c> and every address are
/// 64-bit), and its narrower types (<c>char</c>/<c>short</c>/<c>_Bool</c>) ride in
/// an <c>i32</c> with explicit masking at the narrowing points the emitter inserts
/// — where the C# backend got distinct storage types for free. Floating-point maps
/// onto the two wasm float types (<c>float</c>→<c>f32</c>, <c>double</c>→<c>f64</c>),
/// and pointers/arrays onto <c>i32</c> linear-memory offsets (wasm32).
/// </summary>
internal sealed class WatTarget : ITarget
{
    public string RenderType(CType t) => t.Unqualified switch
    {
        CType.Prim p => RenderPrim(p),
        // An enum is its integer underlying type on the wasm stack.
        CType.Enum e => RenderType(e.Underlying),
        // Addresses are linear-memory offsets (wasm32: i32). Pointer/array support
        // itself arrives with linear memory in milestone 2 — gated before then.
        CType.Pointer or CType.Func or CType.Array => "i32",
        CType.VoidType => throw new IrUnsupportedException("void has no wasm value type"),
        _ => throw new IrUnsupportedException("wat target cannot render type " + t.Describe()),
    };

    /// <summary>A C primitive → its wasm value type: integers narrower than 8 bytes
    /// live in <c>i32</c>, 8-byte integers in <c>i64</c>; <c>float</c> is <c>f32</c>
    /// and <c>double</c>/<c>long double</c> are <c>f64</c> (dotcc models long double
    /// as 64-bit).</summary>
    private static string RenderPrim(CType.Prim p)
    {
        if (!p.Integer)
        {
            return p.Bytes <= 4 ? "f32" : "f64";
        }
        return p.Bytes <= 4 ? "i32" : "i64";
    }

    /// <summary><c>i32.const</c>/<c>i64.const</c> take a bare integer — no C suffix,
    /// the literal's width is carried by the instruction's own type prefix.</summary>
    public string RenderIntLit(LitInt lit) => lit.Digits;

    /// <summary>Render a float constant for <c>f32.const</c>/<c>f64.const</c>. The
    /// neutral <see cref="LitFloat.Text"/> is a C/C# decimal spelling, which differs
    /// from wat's literal syntax in three ways the const grammar rejects: a trailing
    /// <c>f</c>/<c>F</c> suffix (wat carries the width on the instruction prefix, not
    /// the literal), a leading <c>.</c> (<c>.5</c> — wat requires a digit before the
    /// point), and a trailing <c>.</c> (<c>1.</c> — wat requires a digit, or nothing,
    /// after the point but not a dangling one). Exponent forms (<c>1E+21</c>, and now
    /// the point-free <c>1e10</c>) and bare integers (<c>1024</c>) are already legal.</summary>
    public string RenderFloatLit(LitFloat lit)
    {
        var t = lit.Text;
        if (t.Length > 0 && t[^1] is 'f' or 'F') { t = t[..^1]; }
        if (t.Length > 0 && t[0] == '.') { t = "0" + t; }   // .5 -> 0.5
        // A `.` with no fractional digit after it — dangling at the end (`1.`) or
        // sitting right before the exponent (`1.e5`) — is rejected by the wat
        // float-const grammar; splice in a `0` so the point always has a digit.
        var dot = t.IndexOf('.');
        if (dot >= 0 && (dot + 1 == t.Length || t[dot + 1] is 'e' or 'E'))
        {
            t = t[..(dot + 1)] + "0" + t[(dot + 1)..];
        }
        return t;
    }
}

/// <summary>The WebAssembly-text backend's identifier policy — the seam's second
/// <see cref="INameLegalizer"/>, and the one that answers the question the C#-only
/// design left open. A wasm <c>$</c>-name accepts every C identifier character
/// as-is and lives in its own namespace (no reserved words to dodge), so
/// <see cref="Escape"/> is identity — the <c>$</c> sigil is wat syntax the backend
/// prepends, not part of the stored name. But wasm locals are flat and
/// function-scoped, so a C inner-block variable shadowing an outer one must be
/// renamed: <see cref="ForbidsShadowing"/> is true, the SAME answer as C# for a
/// different reason (flat namespace, not CS0136) — confirming the shadow rule
/// belongs to policy, and the table's neutral mechanism needs no per-target change.</summary>
internal sealed class WatNameLegalizer : INameLegalizer
{
    public string Escape(string rawName) => rawName;
    public bool ForbidsShadowing => true;
    public string Uniquify(string escaped, int collision) => $"{escaped}__{collision}";
}

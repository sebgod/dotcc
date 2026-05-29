#nullable enable

namespace DotCC.Libc;

/// <summary>
/// C99/C11 <c>_Bool</c> — an unsigned integer type that holds exactly 0 or 1.
/// Modeled as a distinct value type (NOT C# <c>bool</c>) so it carries C's
/// defining boolean semantics: storing ANY scalar normalizes to 0/1, and the
/// value converts freely to/from <c>int</c> in arithmetic, comparison, return,
/// argument, and <c>%d</c>-printing positions. The implicit conversions below
/// do that at every boundary, so dotcc needs neither a normalization rewrite
/// nor a type-inference pass — and <c>CBool</c> stays distinct from
/// <c>char</c> (also a byte).
/// </summary>
/// <remarks>
/// <c>bool</c> in <c>&lt;stdbool.h&gt;</c> is a macro for <c>_Bool</c>;
/// <c>true</c>/<c>false</c> lower to the integer literals <c>1</c>/<c>0</c>
/// (which normalize through the <c>int</c> conversion). <c>sizeof(_Bool)</c>
/// is 1, matching C. The one case the conversions can't cover is
/// <c>_Bool b = somePointer;</c> — C# forbids user-defined conversions
/// involving pointer types — so a pointer-initialised <c>_Bool</c> is the lone
/// unsupported form (rare; would need an explicit <c>!= NULL</c>).
/// </remarks>
public readonly struct CBool
{
    private readonly byte _v;
    private CBool(byte v) { _v = v; }

    // Store-normalization: any nonzero scalar becomes 1 (C's _Bool conversion).
    public static implicit operator CBool(int x) => new((byte)(x != 0 ? 1 : 0));
    public static implicit operator CBool(long x) => new((byte)(x != 0 ? 1 : 0));
    public static implicit operator CBool(bool b) => new((byte)(b ? 1 : 0));

    // Reads back as int 0/1 — drives arithmetic, comparison, return, args, %d.
    public static implicit operator int(CBool b) => b._v;

    public override string ToString() => _v.ToString();
}

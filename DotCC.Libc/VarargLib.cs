#nullable enable

namespace DotCC.Libc;

// VaArg / VaList are NESTED in Libc (like LongJmpToken) so the predefined-type
// alias the emitter writes — `using unsafe va_list = Libc.VaList;` — resolves,
// while `using static Libc;` still brings them into scope by bare name for the
// `params VaArg[]` / `new VaList(...)` the emitter generates.
public static unsafe partial class Libc
{

/// <summary>
/// One actual argument passed through a C variadic call (<c>...</c>). dotcc
/// lowers a variadic C function <c>T f(fixed…, ...)</c> to a C# method with a
/// trailing <c>params VaArg[]</c>; C# applies the implicit conversions below to
/// each variadic actual at the call site, so the marshalling rides ordinary
/// overload resolution — no boxing, and (unlike <c>object[]</c>) it can carry
/// raw pointers.
/// </summary>
/// <remarks>
/// C's default argument promotions fall out of the conversion set: <c>char</c>
/// (a C# <c>byte</c>) and <c>short</c> widen to <c>int</c> via the standard
/// implicit conversion before reaching <see cref="op_Implicit(int)"/>, and
/// <c>float</c> widens to <c>double</c> before <see cref="op_Implicit(double)"/>
/// — exactly the promotions C performs. A typed <c>T*</c> reaches the
/// <c>void*</c> operator through the standard <c>T* → void*</c> conversion (one
/// standard + one user-defined step, which C# permits). Storage is a single
/// 8-byte integer/pointer slot plus a double slot; <c>va_arg(ap, T)</c> reads
/// back through the matching accessor the emitter picks for <c>T</c>.
/// </remarks>
public readonly struct VaArg
{
    private readonly long _bits;   // integers and pointer addresses
    private readonly double _dbl;  // floating-point values

    private VaArg(long bits, double dbl) { _bits = bits; _dbl = dbl; }

    // FROM each C argument type (applied at the call site by C# on each
    // variadic actual). char/short → int and float → double happen via the
    // standard implicit conversion before reaching these (C's promotions).
    public static implicit operator VaArg(int v) => new(v, 0);
    public static implicit operator VaArg(uint v) => new(v, 0);
    public static implicit operator VaArg(long v) => new(v, 0);
    public static implicit operator VaArg(ulong v) => new(unchecked((long)v), 0);
    public static implicit operator VaArg(double v) => new(0, v);
    public static implicit operator VaArg(CBool v) => new((int)v, 0);
    public static unsafe implicit operator VaArg(void* p) => new((long)p, 0);

    // TO the type requested by `va_arg(ap, T)`, which the emitter lowers to
    // `(T)ap.Next()`. A typedef alias resolves through C# (e.g. `(size_t)` is
    // `(ulong)`), so no per-typedef knowledge is needed here. Pointer reads go
    // via `NextPtr()` → `(T*)(void*)`, a standard explicit pointer conversion.
    public static explicit operator int(VaArg a) => unchecked((int)a._bits);
    public static explicit operator uint(VaArg a) => unchecked((uint)a._bits);
    public static explicit operator long(VaArg a) => a._bits;
    public static explicit operator ulong(VaArg a) => unchecked((ulong)a._bits);
    public static explicit operator double(VaArg a) => a._dbl;
    public static explicit operator float(VaArg a) => (float)a._dbl;
    public static unsafe explicit operator void*(VaArg a) => (void*)a._bits;
}

/// <summary>
/// C <c>va_list</c> — a cursor over the <see cref="VaArg"/> array a variadic
/// function received. A value type, so passing a <c>va_list</c> to another
/// function (the <c>lua_pushfstring</c> → <c>luaO_pushvfstring</c> idiom) and
/// <c>va_copy</c> both work by struct copy: each holder advances its own
/// independent index over the shared (read-only) argument array.
/// </summary>
/// <remarks>
/// <c>va_start(ap, last)</c> lowers to <c>ap = new VaList(_va)</c> (the
/// synthesized params array; <c>last</c> is ignored, as the array already holds
/// exactly the variadic actuals). <c>va_arg(ap, T)</c> lowers to the matching
/// <c>Next…()</c> accessor — pointers via <c>(T)ap.NextPtr()</c>.
/// <c>va_end(ap)</c> → <see cref="End"/> (a no-op). <c>va_copy(d, s)</c> →
/// <c>d = s</c>.
/// </remarks>
public struct VaList
{
    private readonly VaArg[] _args;
    private int _i;

    public VaList(VaArg[] args) { _args = args; _i = 0; }

    // `va_arg(ap, T)` for a scalar T lowers to `(T)ap.Next()`; for a pointer T
    // to `(T)ap.NextPtr()`. Both advance the cursor by one.
    public VaArg Next() => _args[_i++];
    public unsafe void* NextPtr() => (void*)_args[_i++];

    public void End() { }
}

}

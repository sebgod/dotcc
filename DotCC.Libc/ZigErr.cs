#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The runtime backing for the Zig front-end's error unions (<c>E!T</c>) — the
/// value-type half of Milestone B2. Auto-spliced into every emitted program (the
/// <c>DotCC.Libc/*.cs</c> <c>&lt;EmbeddedResource&gt;</c> glob), and compiled into
/// <c>DotCC.Libc.dll</c> for the unit tests, exactly like <see cref="CBool"/>.
/// </summary>
/// <remarks>
/// <para>
/// A Zig <c>!T</c> function returns a <see cref="ErrUnion{T}"/> value: either a
/// payload (<see cref="ErrUnion{T}.Code"/> == 0) or a non-zero error code. dotcc
/// lowers <c>return e;</c> to <c>ErrUnion&lt;T&gt;.Ok(e)</c> and
/// <c>return error.Foo;</c> to <c>ErrUnion&lt;T&gt;.Err(code)</c>.
/// </para>
/// <para>
/// <b>The hard part — early-return-out-of-an-expression.</b> Zig's <c>try e</c>
/// unwraps the payload OR aborts the current function with the error, and it can
/// appear in ANY expression position (<c>const v = try f() + 1;</c>). Structured
/// C# control flow can't express that, so — exactly like the C front-end's
/// <c>setjmp</c>/<c>longjmp</c> lowering (<c>LongJmpException</c>) — dotcc lowers
/// <c>try e</c> to <see cref="ErrUnion.Try{T}"/>, which throws
/// <see cref="ZigErrorReturn"/> on error; the emitted body of each <c>!T</c>
/// function is wrapped in <c>try { … } catch (ZigErrorReturn __e) { return
/// ErrUnion&lt;T&gt;.Err(__e.Code); }</c>, converting the propagated error back to
/// an error-union return. <c>e catch fallback</c> (no propagation) lowers to the
/// plain <see cref="ErrUnion.Catch{T}"/> instead.
/// </para>
/// <para>
/// V1 erases the error SET: every error union shares one flat global code space
/// (so <c>!T</c> / <c>anyerror!T</c> / <c>E!T</c> all lower the same way), and the
/// payload <c>T</c> must be <c>unmanaged</c> (a value type) — an error union over a
/// pointer is deferred (a C# generic can't take a pointer type argument).
/// </para>
/// </remarks>
public readonly struct ErrUnion<T> where T : unmanaged
{
    /// <summary>The error code — 0 means success (a payload is present), any other
    /// value is an error from the flat global error set.</summary>
    public readonly ushort Code;

    /// <summary>The success payload (meaningful only when <see cref="Code"/> == 0;
    /// <c>default</c> otherwise).</summary>
    public readonly T Value;

    private ErrUnion(ushort code, T value) { Code = code; Value = value; }

    /// <summary>True when this union holds an error rather than a payload.</summary>
    public bool IsErr => Code != 0;

    /// <summary>A success union carrying <paramref name="value"/> (<c>return e;</c>).</summary>
    public static ErrUnion<T> Ok(T value) => new(0, value);

    /// <summary>An error union carrying <paramref name="code"/> (<c>return error.Foo;</c>).
    /// <paramref name="code"/> must be non-zero (0 is the success sentinel).</summary>
    public static ErrUnion<T> Err(ushort code) => new(code, default);
}

/// <summary>
/// The unit payload for an error union over <c>void</c> (<c>!void</c>). C# has no
/// <c>Nullable&lt;void&gt;</c> and no generic instantiation over <c>void</c>, so
/// <c>!void</c> lowers to <c>ErrUnion&lt;Unit&gt;</c> — a one-value payload that is
/// always <c>default</c>.
/// </summary>
public readonly struct Unit { }

/// <summary>
/// Thrown by <see cref="ErrUnion.Try{T}"/> to propagate an error out of the current
/// <c>!T</c> function (Zig's <c>try</c>). The function's emitted body is wrapped in a
/// matching <c>catch</c> that converts it back to an <c>ErrUnion&lt;T&gt;.Err</c>.
/// The Zig analogue of <see cref="Libc.LongJmpException"/>, and lowered by the same
/// machinery — early-return-out-of-an-expression that structured C# can't express.
/// </summary>
public sealed class ZigErrorReturn : System.Exception
{
    /// <summary>The propagating error's code (from the originating error union).</summary>
    public ushort Code { get; }

    public ZigErrorReturn(ushort code)
        : base("zig error return") => Code = code;
}

/// <summary>
/// Non-generic helpers for the <c>try</c> / <c>catch</c> lowerings (a sibling of the
/// generic <see cref="ErrUnion{T}"/> value type — same name, different arity, both
/// legal in one namespace).
/// </summary>
public static class ErrUnion
{
    /// <summary><c>try u</c> — yield the payload on success, or propagate the error by
    /// throwing <see cref="ZigErrorReturn"/> (caught at the enclosing <c>!T</c>
    /// function's emitted boundary). An expression, so it works in any position.</summary>
    public static T Try<T>(ErrUnion<T> u) where T : unmanaged
        => u.IsErr ? throw new ZigErrorReturn(u.Code) : u.Value;

    /// <summary><c>u catch fallback</c> — yield the payload on success, else
    /// <paramref name="fallback"/>. No propagation. The lowering only uses this when
    /// <paramref name="fallback"/> is side-effect-free (a literal / variable), so
    /// evaluating it unconditionally is observationally identical to Zig's lazy form.</summary>
    public static T Catch<T>(ErrUnion<T> u, T fallback) where T : unmanaged
        => u.IsErr ? fallback : u.Value;
}

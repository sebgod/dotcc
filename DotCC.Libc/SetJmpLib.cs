#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;setjmp.h&gt;</c> surface, implemented via .NET exceptions.
/// </summary>
/// <remarks>
/// <para>
/// Real C's <c>setjmp</c> "returns twice" — once normally (0) and a
/// second time after <c>longjmp</c> (the longjmp value). That's
/// not expressible in structured C# control flow. The dotcc lowering
/// recognises specific syntactic patterns where <c>setjmp</c> appears
/// in an <c>if</c> condition and rewrites the whole <c>if/else</c>
/// into a <c>try / catch when</c> block. <c>longjmp</c> throws a
/// <see cref="LongJmpException"/> carrying the token + value; the
/// <c>catch when</c> filter matches the right setjmp.
/// </para>
/// <para>
/// Supported syntactic shapes (the emitter recognises these and
/// rewrites; other shapes throw <c>CompileException</c>):
/// <list type="bullet">
///   <item><c>if (setjmp(env)) { recovery } else { normal }</c></item>
///   <item><c>if (setjmp(env) == 0) { normal } else { recovery }</c></item>
/// </list>
/// One bonus over real C: finally blocks DO run during the longjmp
/// unwind (.NET exception semantics). That's strictly better than
/// real <c>longjmp</c>, which silently skips through cleanup code —
/// a famous footgun.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>
    /// Opaque token identifying a particular <c>setjmp</c> site. User
    /// code declares <c>jmp_buf env;</c>; the synthetic header
    /// typedefs <c>jmp_buf</c> to this class, so the C-level type
    /// stays opaque while the C# side has a real identity.
    /// </summary>
    public sealed class LongJmpToken { }

    /// <summary>
    /// Exception carrying a non-local jump's target + value. Thrown
    /// by <see cref="longjmp(LongJmpToken, int)"/>; caught by the
    /// emitter-generated <c>catch when (__jmp.Token == env)</c>.
    /// </summary>
    public sealed class LongJmpException : Exception
    {
        public LongJmpToken Token { get; }
        public int Value { get; }
        public LongJmpException(LongJmpToken token, int value)
            : base($"longjmp(value={value})")
        {
            Token = token;
            Value = value;
        }
    }

    /// <summary>
    /// <c>setjmp(env)</c>. The function ALWAYS returns 0 on its
    /// direct call (it's the post-longjmp re-entry that returns the
    /// value, and that's handled by the emitter's try/catch
    /// rewrite). User code that pattern-matches the result against
    /// 0 vs non-zero gets the right behaviour after the rewrite.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int setjmp(LongJmpToken env) => 0;

    /// <summary>
    /// <c>longjmp(env, value)</c>. Throws a
    /// <see cref="LongJmpException"/> tagged with <paramref name="env"/>.
    /// The emitter-generated <c>catch when (__jmp.Token == env)</c>
    /// at the matching <c>setjmp</c> site catches it, exposes the
    /// value, and resumes execution in the recovery branch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void longjmp(LongJmpToken env, int value)
        => throw new LongJmpException(env, value == 0 ? 1 : value);
}

#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;assert.h&gt;</c> surface. The C macro <c>assert(expr)</c>
/// in dotcc's synthetic header expands to <c>__dotcc_assert(expr)</c>;
/// this class provides overloads of that function so any expression
/// type C can use as a truthy condition (int, bool, double, pointer)
/// resolves through C#'s overload resolution at the call site.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CallerArgumentExpressionAttribute"/> on each overload's
/// <c>expr</c> parameter means the C# compiler inlines the SOURCE TEXT
/// of the condition expression at every call site — so when the assert
/// fires the error message includes <c>"x &gt; 0"</c> rather than just
/// "false". That gives us glibc-style diagnostic quality without
/// needing the preprocessor to stringify anything.
/// </para>
/// <para>
/// Failed assertions throw an <see cref="AssertionFailedException"/>
/// rather than calling <c>Environment.FailFast</c> — the throw makes
/// the failure observable to tests and lets <c>finally</c> blocks run.
/// Real C's <c>abort()</c> behaviour can be approximated by catching at
/// <c>main</c> and exiting; the standard intention ("stop the program
/// now") is preserved.
/// </para>
/// <para>
/// When <c>NDEBUG</c> is defined before <c>#include &lt;assert.h&gt;</c>,
/// the synthetic header expands <c>assert(expr)</c> to <c>((void)0)</c>
/// — this class's methods aren't called at all in that mode.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>
    /// Assertion-failed signal. Thrown by <c>assert(expr)</c> when the
    /// condition is falsy and <c>NDEBUG</c> isn't defined. Carries the
    /// original condition source text in <see cref="ConditionText"/>.
    /// </summary>
    public sealed class AssertionFailedException : Exception
    {
        public string ConditionText { get; }
        public AssertionFailedException(string conditionText)
            : base($"Assertion failed: {conditionText}")
        {
            ConditionText = conditionText;
        }
    }

    /// <summary>
    /// NDEBUG-mode assert: no-op. The synthetic <c>&lt;assert.h&gt;</c>'s
    /// NDEBUG branch expands <c>assert(expr)</c> to a call here so the
    /// expression isn't evaluated (matching C99 §7.2.1.1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void __dotcc_assert_noop() { }

    /// <summary><c>assert</c> for <c>int</c>-typed conditions (the C primary).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void __dotcc_assert(int condition,
        [CallerArgumentExpression(nameof(condition))] string expr = null!)
    {
        if (condition == 0)
        {
            throw new AssertionFailedException(expr ?? "?");
        }
    }

    /// <summary><c>assert</c> for <c>bool</c>-typed conditions (C99 <c>_Bool</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void __dotcc_assert(bool condition,
        [CallerArgumentExpression(nameof(condition))] string expr = null!)
    {
        if (!condition)
        {
            throw new AssertionFailedException(expr ?? "?");
        }
    }

    /// <summary><c>assert</c> for <c>double</c>-typed conditions (rare in real C, but covered for completeness).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void __dotcc_assert(double condition,
        [CallerArgumentExpression(nameof(condition))] string expr = null!)
    {
        if (condition == 0)
        {
            throw new AssertionFailedException(expr ?? "?");
        }
    }

    /// <summary><c>assert</c> for pointer-typed conditions — <c>assert(p)</c> in C is "p != NULL".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void __dotcc_assert(void* condition,
        [CallerArgumentExpression(nameof(condition))] string expr = null!)
    {
        if (condition == null)
        {
            throw new AssertionFailedException(expr ?? "?");
        }
    }
}

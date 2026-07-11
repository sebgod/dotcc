#nullable enable

namespace DotCC.Libc;

/// <summary>
/// Runtime backing for the curated <c>std.testing</c> assertions used by
/// <c>dotcc zig test</c>. Each returns an <see cref="ErrUnion{T}"/> over <see cref="Unit"/>
/// (Zig's <c>!void</c>): success (<c>Ok</c>) when the assertion holds, else an error (<c>Err</c>)
/// carrying a fixed sentinel code the test runner reports on FAIL. The codes are deliberately NOT
/// part of the program's <c>@errorName</c> registry (the runner never resolves them to a name) —
/// they only have to be non-zero and distinct. Auto-spliced into every emitted program (the
/// <c>DotCC.Libc/*.cs</c> <c>&lt;EmbeddedResource&gt;</c> glob) and compiled into
/// <c>DotCC.Libc.dll</c> for the unit tests, exactly like <see cref="ErrUnion{T}"/> / <see cref="CBool"/>.
/// </summary>
/// <remarks>
/// The curated subset mirrors the campaign's "grow on purpose" stance: only the two most common
/// assertions are modeled today (<c>expect</c>, <c>expectEqual</c>); any other <c>std.testing</c>
/// member is a clear, specific cut at the lowering site. <c>expectEqual</c> requires the two operands
/// to be the same <c>unmanaged</c> + <see cref="System.IEquatable{T}"/> type (C# infers <c>T</c> from
/// the call), which covers the integer / float / bool / enum scalars a leaf test compares.
/// </remarks>
public static class ZigTesting
{
    /// <summary><c>error.TestUnexpectedResult</c> — a failed <c>expect</c>.</summary>
    public const ushort TestUnexpectedResult = 0xFF01;

    /// <summary><c>error.TestExpectedEqual</c> — a failed <c>expectEqual</c>.</summary>
    public const ushort TestExpectedEqual = 0xFF02;

    /// <summary><c>std.testing.expect(ok)</c> — <c>Ok</c> when <paramref name="ok"/> is true,
    /// else an error union carrying <see cref="TestUnexpectedResult"/>. Takes <see cref="CBool"/>
    /// (not <c>bool</c>) because dotcc's boolean type (<c>CType.Bool</c>) lowers to <see cref="CBool"/>,
    /// which converts to <c>int</c> but not directly to <c>bool</c>.</summary>
    public static ErrUnion<Unit> expect(CBool ok) =>
        (int)ok != 0 ? ErrUnion<Unit>.Ok(default) : ErrUnion<Unit>.Err(TestUnexpectedResult);

    /// <summary><c>std.testing.expectEqual(expected, actual)</c> — <c>Ok</c> when the two are
    /// equal, else an error union carrying <see cref="TestExpectedEqual"/>.</summary>
    public static ErrUnion<Unit> expectEqual<T>(T expected, T actual) where T : unmanaged, System.IEquatable<T> =>
        expected.Equals(actual) ? ErrUnion<Unit>.Ok(default) : ErrUnion<Unit>.Err(TestExpectedEqual);
}

#nullable enable

using System.Collections.Generic;
using DotCC.Ir;
using LALR.CC.LexicalGrammar;

namespace DotCC.Frontends;

/// <summary>Curated <c>std.testing</c> assertions for <c>dotcc zig test</c>. Like <c>std.mem</c> /
/// <c>std.debug.print</c>, this is a curated-paths resolver, not a general <c>std</c> model — only the
/// two most common assertions are lowered; anything else is a loud, specific cut.
///
/// <para>Each assertion lowers to a call of the runtime <see cref="DotCC.Libc.ZigTesting"/> helper,
/// typed as an error union over <c>void</c> (<c>ErrUnion&lt;Unit&gt;</c>). A test body reaches them
/// through <c>try std.testing.expect(…)</c>: on failure the returned <c>Err</c> makes <c>try</c>
/// propagate a <c>ZigErrorReturn</c> to the test function's boundary, which the generated test runner
/// observes as a failing test.</para></summary>
internal sealed partial class ZigLowering
{
    /// <summary>Lower a curated <c>std.testing</c> call. <c>expect(ok: bool)</c> and
    /// <c>expectEqual(expected, actual)</c> are modeled; any other member is a clear cut.</summary>
    private CExpr LowerStdTestingCall(string method, IReadOnlyList<Item> argItems)
    {
        var errUnionVoid = new CType.ErrorUnion(CType.Void);
        switch (method)
        {
            case "expect":
                if (argItems.Count != 1)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.testing.expect` expects one argument (ok: bool); got {argItems.Count}");
                }
                var cond = LowerExprSink(argItems[0], CType.Bool);
                return new Call("ZigTesting.expect", new List<CExpr> { cond }, new List<CType> { CType.Bool })
                {
                    Type = errUnionVoid,
                };
            case "expectEqual":
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.testing.expectEqual` expects two arguments (expected, actual); got {argItems.Count}");
                }
                // Both operands lower with their natural types; C# infers the generic `T` at the
                // emitted call. Real Zig also requires `@TypeOf(expected) == @TypeOf(actual)`.
                var expected = LowerExpr(argItems[0]);
                var actual = LowerExpr(argItems[1]);
                return new Call("ZigTesting.expectEqual", new List<CExpr> { expected, actual })
                {
                    Type = errUnionVoid,
                };
            default:
                throw new IrUnsupportedException(
                    $"zig `std.testing.{method}` is not supported yet — `dotcc zig test` models `expect` and "
                    + "`expectEqual` (road-to-zig-std; more assertions grow on demand)");
        }
    }
}

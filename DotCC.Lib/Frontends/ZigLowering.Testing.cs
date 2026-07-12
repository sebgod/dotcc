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
            case "expectError":
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.testing.expectError` expects two arguments (expected_error, actual); got {argItems.Count}");
                }
                // `error.X` lowers to its flat code (CType.ErrorSet → ushort); the actual result must
                // be an error union (the un-`try`'d result of a fallible call), whose code is compared.
                var expectedErr = LowerExpr(argItems[0]);
                var actualResult = LowerExpr(argItems[1]);
                if (actualResult.Type.Unqualified is not CType.ErrorUnion)
                {
                    throw new IrUnsupportedException(
                        "zig `std.testing.expectError` expects an error-union second argument (a fallible "
                        + $"call's result, not `try`'d), got {actualResult.Type.Describe()}");
                }
                return new Call("ZigTesting.expectError", new List<CExpr> { expectedErr, actualResult })
                {
                    Type = errUnionVoid,
                };
            case "expectEqualStrings":
                if (argItems.Count != 2)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.testing.expectEqualStrings` expects two arguments (expected, actual); got {argItems.Count}");
                }
                var strSink = new CType.Slice(CType.UChar.WithQuals(TypeQual.Const));   // []const u8
                var strA = LowerExprSink(argItems[0], strSink);
                var strB = LowerExprSink(argItems[1], strSink);
                return new Call("ZigTesting.expectEqualStrings", new List<CExpr> { strA, strB })
                {
                    Type = errUnionVoid,
                };
            case "expectEqualSlices":
                if (argItems.Count != 3)
                {
                    throw new IrUnsupportedException(
                        $"zig `std.testing.expectEqualSlices` expects three arguments (T, expected, actual); got {argItems.Count}");
                }
                // First argument is the element TYPE (like `std.mem.eql`); C# infers the generic `T`
                // from the coerced slice operands, so it is not passed through to the emitted call.
                var elem = LowerType(argItems[0]).Unqualified;
                var sliceSink = new CType.Slice(elem.WithQuals(TypeQual.Const));
                var slA = LowerExprSink(argItems[1], sliceSink);
                var slB = LowerExprSink(argItems[2], sliceSink);
                return new Call("ZigTesting.expectEqualSlices", new List<CExpr> { slA, slB })
                {
                    Type = errUnionVoid,
                };
            default:
                throw new IrUnsupportedException(
                    $"zig `std.testing.{method}` is not supported yet — `dotcc zig test` models `expect`, "
                    + "`expectEqual`, `expectError`, `expectEqualStrings`, and `expectEqualSlices` "
                    + "(road-to-zig-std; more assertions grow on demand)");
        }
    }
}

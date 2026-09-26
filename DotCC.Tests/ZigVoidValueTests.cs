#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for zig's VOID value <c>{}</c> and <c>void</c>-typed storage (road-to-zig-std S9; std passes
/// <c>{}</c> 108 times, the <c>context: void</c> of <c>std.sort</c>). zig's void is zero-sized with no
/// runtime value, and C# has no void parameter, local or <c>default(void)</c>, so the emit ERASES it: a
/// void parameter from signatures, calls and <c>delegate*</c> types, a void local and a store into one,
/// a discard of one, and <c>return {};</c> as a bare <c>return;</c>. Before this a <c>void</c> parameter
/// emitted <c>byte f(void ctx, byte x)</c>, a C# error from a dotcc run that exited 0.
/// End-to-end in the <c>void_value</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigVoidValueTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigvoid-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_void_parameter_is_erased_from_the_signature_the_call_and_the_fn_pointer_type()
    {
        var cs = EmitZig("""
            fn lessThan(context: void, a: u8, b: u8) bool {
                _ = context;
                return a < b;
            }
            fn larger(context: anytype, a: u8, b: u8, less: *const fn (@TypeOf(context), u8, u8) bool) u8 {
                return if (less(context, a, b)) b else a;
            }
            pub fn main() u8 {
                return larger({}, 42, 7, &lessThan);
            }
            """);
        cs.ShouldContain("static unsafe CBool lessThan(byte a, byte b)");
        cs.ShouldContain("larger__void(byte a, byte b, delegate*<byte, byte, CBool> less)");
        cs.ShouldContain("larger__void(42, 7, &lessThan)");
        cs.ShouldContain("less(a, b)");
        cs.ShouldNotContain("void context");
    }

    [Fact]
    public void Void_storage_and_a_void_return_emit_nothing_to_spell()
    {
        var cs = EmitZig("""
            fn nothing() void {
                return {};
            }
            pub fn main() u8 {
                var unit: void = {};
                unit = {};
                const v = {};
                _ = v;
                nothing();
                return 42;
            }
            """);
        cs.ShouldNotContain("default(void)");
        cs.ShouldNotContain("void unit");
        cs.ShouldContain("return;");
    }

    [Fact]
    public void A_side_effecting_argument_to_a_void_parameter_is_rejected()
    {
        // Erasing it would silently drop the call.
        var ex = Should.Throw<CompileException>(() => EmitZig("""
            fn effect() void {}
            fn take(ctx: void, x: u8) u8 {
                _ = ctx;
                return x;
            }
            pub fn main() u8 {
                return take(effect(), 42);
            }
            """));
        ex.Message.ShouldContain("a side-effecting argument to a `void` parameter");
    }
}

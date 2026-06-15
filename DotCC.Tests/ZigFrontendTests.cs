#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// End-to-end vertical slice for the Zig front-end: a <c>.zig</c> input routes through
/// <c>ZigFrontend</c> (parse → <c>ZigLowering</c> → neutral IR) and the existing C#
/// backend + shell, exactly like a C input. Proves the <c>IFrontend</c> seam works with
/// a real second implementer, not just structurally.
/// </summary>
public sealed class ZigFrontendTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zig-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Lowers_zig_main_end_to_end()
    {
        // pub fn main() u8 { const x: u8 = 40; return x + 2; }  → a byte-returning
        // C# main computing 42, wired as the entry point by the shared shell.
        var cs = EmitZig("pub fn main() u8 {\n    const x: u8 = 40;\n    return x + 2;\n}\n");
        cs.ShouldContain("main");        // the function lowered + recognised as entry
        cs.ShouldContain("40");          // the const initializer
        cs.ShouldContain("x + 2");       // the arithmetic, preserved
        cs.ShouldContain("return");
    }

    [Fact]
    public void Lowers_parameters_into_the_signature()
    {
        // A function with parameters: the names + types ride into the C# signature,
        // and the param refs resolve in the body. (`wide` is uncalled — calls aren't
        // lowered yet — but still emitted, so its signature is checked here.)
        var cs = EmitZig("fn wide(a: i64, b: i64) i64 { return a * b; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("wide(long a, long b)");
        cs.ShouldContain("a * b");
    }

    [Fact]
    public void Lowers_integer_signedness_faithfully()
    {
        // i8 → sbyte (signed), u16 → ushort — the slice collapsed both 8-bit forms
        // to byte; signedness is now distinct.
        var cs = EmitZig("fn f(a: i8, b: u16) i8 { return a; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("f(sbyte a, ushort b)");
    }

    [Fact]
    public void Lowers_if_and_while_statements()
    {
        // if/else + while lower to the C# forms, conditions wrapped in Cond.B for
        // C-truthy semantics (shared with the C backend).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var x: u8 = 0;\n" +
            "    if (3 > 2) { x = 42; } else { x = 1; }\n" +
            "    while (x > 100) { x = x + 1; }\n" +
            "    return x;\n}\n");
        cs.ShouldContain("if (Cond.B(");
        cs.ShouldContain("else");
        cs.ShouldContain("while (Cond.B(");
    }

    [Fact]
    public void Lowers_if_expression_to_a_ternary()
    {
        // const y = if (c) a else b;  → a C# ternary with the condition in Cond.B.
        var cs = EmitZig("pub fn main() u8 { const x: u8 = 40; const y: u8 = if (x > 10) x else 0; return y + 2; }\n");
        cs.ShouldContain("Cond.B(");
        cs.ShouldContain("? ");
        cs.ShouldContain(" : ");
    }

    [Fact]
    public void Lowers_a_function_call_including_forward_reference()
    {
        // main calls add, which is defined AFTER it — the two-pass lowering declares
        // every signature before lowering any body, so the forward reference resolves.
        var cs = EmitZig("pub fn main() u8 { return add(40, 2); }\nfn add(a: u8, b: u8) u8 { return a + b; }\n");
        cs.ShouldContain("add(40, 2)");           // the call site (fitting constants aren't cast)
        cs.ShouldContain("add(byte a, byte b)");  // the callee signature
    }

    [Fact]
    public void Lowers_extern_fn_libc_call()
    {
        // `extern fn putchar(c: c_int) c_int;` declares a libc prototype (no body); the
        // call routes by bare name to dotcc's Libc runtime — same as a C program's libc
        // call. `_ = putchar(72);` is the Zig discard of a non-void result.
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 { _ = putchar(72); return 0; }\n");
        cs.ShouldContain("putchar(72)");   // the call site (libc fn, bare name)
    }

    [Fact]
    public void Lowers_variadic_printf_to_the_fluent_builder()
    {
        // `extern fn printf(format: [*c]const u8, ...) c_int;` — the variadic libc
        // prototype. The `[*c]const u8` format param lowers to `byte*`, the `"…"`
        // literal to the same pooled `Libc.L(…)` pointer a C string gets, and the call
        // routes through the printf-family fluent builder: the format is the fixed arg,
        // and the variadic argument — `@as(c_int, 42)`, since a bare literal is rejected
        // (see Rejects_… below) — rides the `.Arg(…)` tail as the cast value `(int)42`.
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 { _ = printf(\"Hi %d\\n\", @as(c_int, 42)); return 0; }\n");
        cs.ShouldContain("printf(Libc.L(");   // format → pooled UTF-8 pointer, fluent head
        cs.ShouldContain(".Arg((int)42)");     // the variadic argument: @as(c_int, 42) → (int)42
        cs.ShouldContain(".Done()");           // builder terminator
    }

    [Fact]
    public void Rejects_a_bare_literal_passed_to_a_variadic_function()
    {
        // Zig parity (the differential oracle caught dotcc being too lenient): an untyped
        // comptime literal has no fixed-size ABI type, so it cannot cross a C-variadic
        // boundary — real zig errors, and dotcc must too. `@as(c_int, 42)` (above) or any
        // concretely-typed value is required. Locks the strictness in WITHOUT needing the
        // (opt-in) oracle, so a future leniency regression fails the always-on suite.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 { _ = printf(\"Hi %d\\n\", 42); return 0; }\n"));
        ex.Message.ShouldContain("must be casted to a fixed-size number type");
    }

    [Fact]
    public void Lowers_pointer_deref_index_and_address_of()
    {
        // The postfix/prefix ops on the pointer types we already have, each reusing the
        // C IR: `p[i]` → Index, `px.*` → Unary(Deref), `&x` → Unary(AddrOf). A `[*c]const
        // u8` param indexes like a C `const char*`; `&x` of a local takes its address and
        // `px.*` reads back through it.
        var cs = EmitZig(
            "fn first(p: [*c]const u8) u8 { return p[0]; }\n" +
            "pub fn main() u8 { var x: u8 = 7; const px: *u8 = &x; return px.* + first(\"hi\"); }\n");
        cs.ShouldContain("p[0]");   // subscript on a C pointer → Index
        cs.ShouldContain("&x");     // address-of a local → Unary(AddrOf)
        cs.ShouldContain("*px");    // pointer deref `px.*` → Unary(Deref)
    }

    [Fact]
    public void Lowers_void_returning_main()
    {
        // `pub fn main() void` — idiomatic Zig (no exit code). The shell can't
        // `return main()` from its int-typed entry, so it calls main for effect and
        // returns 0. main needs no explicit `return;` (a void body just falls off).
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() void { _ = printf(\"ok\\n\"); }\n");
        cs.ShouldContain("void main()");      // the function lowered with a void return
        cs.ShouldContain("main(); return 0;"); // entry wiring: call for effect, exit 0
        cs.ShouldNotContain("return main();"); // NOT the int-main form
    }

    [Fact]
    public void Lowers_value_optional_to_csharp_nullable()
    {
        // A `?T` over a value type → C# Nullable<T> (`T?`): `null` is none, `.?` is
        // `.Value` (panics on none), and `orelse` is the null-coalescing `??` — single
        // evaluation of the left, lazy right, exactly Zig's semantics. No custom runtime.
        var cs = EmitZig(
            "pub fn main() u8 { const a: ?i32 = null; const b: ?i32 = 5; " +
            "return @as(u8, (a orelse 0) + b.?); }\n");
        cs.ShouldContain("int? a");   // ?i32 → C# int?
        cs.ShouldContain("??");        // orelse → null-coalescing
        cs.ShouldContain(".Value");    // .? → Nullable.Value (panics on none)
    }

    [Fact]
    public void Lowers_optional_pointer_as_a_bare_nullable_pointer()
    {
        // A `?*T` is Zig's niche optional — lowered to a bare `T*` (null = none), so it
        // reuses all the existing pointer machinery. `null` is the null pointer, and
        // `orelse` becomes `p != null ? p : d` (C# `??` doesn't apply to pointers).
        var cs = EmitZig(
            "pub fn main() u8 { var x: u8 = 7; const p: ?*u8 = null; " +
            "const r: *u8 = p orelse &x; return r.*; }\n");
        cs.ShouldContain("byte* p");   // ?*u8 → bare byte* (the niche)
        cs.ShouldContain("!= null");   // pointer orelse → null test
    }

    [Fact]
    public void Lowers_error_union_return_try_and_propagation()
    {
        // A `!u8` function (error-union return) lowers to `ErrUnion<byte>`: `return error.X`
        // → `.Err(code)`, a plain `return e` → `.Ok(e)`. A caller's `try f()` lowers to
        // `ErrUnion.Try(f())` (unwrap-or-propagate), and EVERY error-union body is wrapped in
        // a `catch (ZigErrorReturn …)` that converts a propagated error back to an Err return
        // — the exception-based early-return-out-of-an-expression, modeled on the setjmp lowering.
        var cs = EmitZig(
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x + 1; }\n" +
            "fn outer(x: u8) !u8 { const v = try parse(x); return v + 1; }\n" +
            "pub fn main() u8 { return outer(40) catch 0; }\n");
        cs.ShouldContain("ErrUnion<byte> parse");   // `!u8` return → ErrUnion<byte>
        cs.ShouldContain("ErrUnion<byte>.Err(");     // `return error.Zero` → Err(code)
        cs.ShouldContain("ErrUnion<byte>.Ok(");      // `return x + 1` → Ok(payload)
        cs.ShouldContain("ErrUnion.Try(");           // `try parse(x)` → unwrap-or-propagate
        cs.ShouldContain("catch (ZigErrorReturn");   // the per-function propagation boundary
    }

    [Fact]
    public void Lowers_catch_to_the_catch_helper()
    {
        // `f() catch fallback` → `ErrUnion.Catch<P>(f(), fallback)` — the payload on success,
        // else the (side-effect-free) fallback. The type argument is explicit so a fitting
        // constant fallback (`catch 0`) converts to the payload rather than clashing as int.
        var cs = EmitZig(
            "fn parse(x: u8) !u8 { if (x == 0) return error.Zero; return x; }\n" +
            "pub fn main() u8 { return parse(0) catch 0; }\n");
        cs.ShouldContain("ErrUnion.Catch<byte>(");   // explicit payload type arg
    }

    [Fact]
    public void Lowers_error_union_void()
    {
        // `!void` has no generic-over-void in C#, so it lowers to `ErrUnion<Unit>`. A body
        // that falls off the end is a Zig success → a trailing `ErrUnion<Unit>.Ok(default)`.
        var cs = EmitZig(
            "fn check(x: u8) !void { if (x == 0) return error.Zero; }\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("ErrUnion<Unit> check");        // `!void` → ErrUnion<Unit>
        cs.ShouldContain("ErrUnion<Unit>.Err(");          // `return error.Zero`
        cs.ShouldContain("ErrUnion<Unit>.Ok(default)");   // fall-off success
    }

    [Fact]
    public void Rejects_try_on_a_non_error_union_value()
    {
        // `try` is only meaningful on an error union; applying it to a plain value is a
        // structural error (real zig: "expected error union type"). dotcc fails loudly
        // rather than miscompiling. (IrUnsupportedException is a CompileException subclass.)
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 { const x: u8 = 5; return try x; }\n"));
        ex.Message.ShouldContain("error-union");
    }
}

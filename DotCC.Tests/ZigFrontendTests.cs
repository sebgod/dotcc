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
[Collection("ZigFrontend")]
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

    [Fact]
    public void Lowers_while_continue_expression_to_a_for()
    {
        // `while (cond) : (cont) body` → the C IR `For` (no init), so the cont runs after
        // each iteration AND on `continue` — exactly C's for-update semantics. The `i = i + 1`
        // assignment cont becomes the for-post.
        var cs = EmitZig(
            "pub fn main() u8 { var i: u8 = 0; while (i < 10) : (i = i + 1) { } return i; }\n");
        cs.ShouldContain("for (;");              // lowered to a for (no init)
        cs.ShouldContain("i = (byte)(i + 1)");   // the continue-expression as the for-post
    }

    [Fact]
    public void Lowers_break_and_continue()
    {
        // `break;` / `continue;` reuse the C IR loop-control nodes; the C# backend renders
        // them verbatim. `continue` in a `while : (cont)` runs the cont (the for-post).
        var cs = EmitZig(
            "pub fn main() u8 { var i: u8 = 0; while (i < 10) : (i = i + 1) { " +
            "if (i == 2) continue; if (i == 5) break; } return i; }\n");
        cs.ShouldContain("continue;");
        cs.ShouldContain("break;");
    }

    [Fact]
    public void Lowers_switch_with_multi_value_and_else()
    {
        // `switch (x) { 0 => {…}, 1, 2 => {…}, else => {…} }` → the C IR Switch: single value
        // → one `case`, a multi-value prong → stacked `case`s, `else` → `default`. Zig has no
        // fall-through, so each section ends in an appended `break;` (no synthetic goto-next).
        var cs = EmitZig(
            "pub fn main() u8 { var r: u8 = 0; switch (r) { " +
            "0 => { r = 10; }, 1, 2 => { r = 20; }, else => { r = 30; }, } return r; }\n");
        cs.ShouldContain("switch (");
        cs.ShouldContain("case 0:");
        cs.ShouldContain("case 1:");
        cs.ShouldContain("case 2:");   // multi-value prong → stacked labels
        cs.ShouldContain("default:");  // `else` → default
        cs.ShouldContain("break;");    // no Zig fall-through
    }

    [Fact]
    public void Lowers_for_range_to_a_counted_for()
    {
        // `for (0..n) |i| body` → C `for (ulong i = 0; i < (ulong)n; i++) body`. The `|i|`
        // capture is the usize loop index; the end is cast to ulong so the comparison is
        // unsigned-clean (C# won't compare ulong with a signed operand).
        var cs = EmitZig(
            "pub fn main() u8 { var s: u8 = 0; for (0..10) |i| { if (i == 7) { s = 1; } } return s; }\n");
        cs.ShouldContain("for (ulong i = 0;");   // counted for over the usize index
        cs.ShouldContain("i < (ulong)");          // unsigned-clean bound comparison
        cs.ShouldContain("i++");                  // the increment
    }

    [Fact]
    public void Lowers_struct_decl_field_access_and_anonymous_literal()
    {
        // `const Point = struct { x: i32, y: i32 };` → a real C# `unsafe struct Point` with
        // typed fields (the SAME shared aggregate machinery the C frontend uses). A typed
        // `const p: Point = .{ .x = 40, .y = 2 };` is Zig's result-located literal → a C#
        // object initializer `new Point { x = 40, y = 2 }`; `p.x` reads the field by type.
        var cs = EmitZig(
            "const Point = struct { x: i32, y: i32 };\n" +
            "pub fn main() u8 { const p: Point = .{ .x = 40, .y = 2 }; return @as(u8, p.x + p.y); }\n");
        cs.ShouldContain("unsafe struct Point");   // struct → C# struct (shared StructText)
        cs.ShouldContain("public int x;");          // i32 field
        cs.ShouldContain("new Point {");            // `.{…}` → object initializer
        cs.ShouldContain("x = 40");                 // designated field init
        cs.ShouldContain("p.x");                    // field access by type
        cs.ShouldContain("p.y");
    }

    [Fact]
    public void Lowers_typed_struct_literal_in_value_and_sink_less_positions()
    {
        // The TYPED form `Point{ .x = … }` (Zig's CurlySuffixExpr) names its own type, so —
        // unlike the anonymous `.{…}` — it needs no sink and is valid in a sink-less position
        // such as an immediate field access `(Point{…}).y`. Both lower to a C# object
        // initializer (`(Point{…}).y` → `new Point { … }.y`, member access on the literal).
        var cs = EmitZig(
            "const Point = struct { x: i32, y: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point{ .x = 40, .y = 2 };\n" +
            "    const j = (Point{ .x = 5, .y = 9 }).y;\n" +
            "    return @as(u8, p.x + p.y - 9 + j); }\n");
        cs.ShouldContain("new Point {");   // typed literal → object initializer
        cs.ShouldContain("x = 40");        // value-position literal
        cs.ShouldContain("}.y");           // sink-less immediate field access on the literal
    }

    [Fact]
    public void Lowers_addr_of_typed_struct_literal_via_a_temp()
    {
        // `&Point{ … }` — address of a temporary. C# forbids `&new T{…}` (CS0211), so the
        // literal is materialized to a block-local temp and ITS address is taken — the same
        // shared-backend path C compound literals `&(T){…}` use. Here as a `*Point` argument.
        var cs = EmitZig(
            "const Point = struct { x: i32, y: i32 };\n" +
            "fn sum(p: *const Point) i32 { return p.x + p.y; }\n" +   // &literal is *const T in Zig
            "pub fn main() u8 { return @as(u8, sum(&Point{ .x = 40, .y = 2 })); }\n");
        cs.ShouldContain("new Point {");   // literal materialized…
        cs.ShouldContain("__cl");          // …into a block-local temp
        cs.ShouldContain("&__cl");         // address of the temp (not `&new T{…}`)
    }

    [Fact]
    public void Lowers_struct_field_access_through_a_pointer_with_arrow()
    {
        // Zig has no `->`: `p.x` on a `*Point` auto-derefs. The shared `Member` node carries
        // an arrow flag, so a pointer base emits C#'s `p->x` (valid on an unsafe struct ptr).
        var cs = EmitZig(
            "const Point = struct { x: i32, y: i32 };\n" +
            "fn getx(p: *Point) i32 { return p.x; }\n" +
            "pub fn main() u8 { var pt: Point = .{ .x = 7, .y = 0 }; return @as(u8, getx(&pt)); }\n");
        cs.ShouldContain("getx(Point* p)");   // *Point param
        cs.ShouldContain("p->x");              // pointer field access auto-derefs
    }

    [Fact]
    public void Lowers_enum_decl_and_dotted_member_access()
    {
        // `const Color = enum(u8) { red, green, blue };` → a C# `enum Color : byte` with
        // auto-incremented members. `Color.blue` resolves to an EnumConstRef → `Color.blue`;
        // `@intFromEnum` decays it to the underlying integer (the C-enum→int decay reused).
        var cs = EmitZig(
            "const Color = enum(u8) { red, green, blue };\n" +
            "pub fn main() u8 { return @intFromEnum(Color.blue); }\n");
        cs.ShouldContain("enum Color : byte");   // enum → C# enum with underlying
        cs.ShouldContain("red = 0,");             // auto-increment from 0
        cs.ShouldContain("blue = 2,");
        cs.ShouldContain("Color.blue");           // dotted member access
    }

    [Fact]
    public void Lowers_enum_literal_at_a_typed_sink_and_explicit_value()
    {
        // A bare `.green` literal resolves against its sink: a typed `const c: Color`. An
        // explicit member value (`two = 2`) is const-evaluated; the next bare member
        // auto-increments from it (`three` = 3).
        var cs = EmitZig(
            "const N = enum(u8) { zero, two = 2, three };\n" +
            "const Color = enum { red, green };\n" +
            "pub fn main() u8 { const c: Color = .green; return @intFromEnum(c) + @intFromEnum(N.three); }\n");
        cs.ShouldContain("two = 2,");
        cs.ShouldContain("three = 3,");    // auto-increment continues from the explicit value
        cs.ShouldContain("Color.green");   // `.green` resolved at the typed sink
    }

    [Fact]
    public void Lowers_switch_on_an_enum_with_dotted_cases()
    {
        // `switch (c) { .red => …, else => … }` on an enum: the subject + case labels decay
        // to the underlying integer (shared enum-switch lowering), so the labels render as
        // `case (int)Color.red:` and `default:` (the `else`).
        var cs = EmitZig(
            "const Color = enum { red, green, blue };\n" +
            "fn rank(c: Color) u8 { switch (c) { .red => { return 1; }, .green => { return 2; }, else => { return 9; }, } }\n" +
            "pub fn main() u8 { return rank(.green); }\n");
        cs.ShouldContain("switch (");
        cs.ShouldContain("Color.red");    // dotted case label (decayed: `case (int)Color.red:`)
        cs.ShouldContain("default:");      // `else` prong
    }

    [Fact]
    public void Rejects_an_unknown_struct_field()
    {
        // A `.field` not declared on the struct is a structural error (real zig: "no field
        // named …"). dotcc fails loudly via the shared field-type lookup rather than guessing.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Point = struct { x: i32 };\n" +
            "pub fn main() u8 { const p: Point = .{ .x = 1 }; return @as(u8, p.z); }\n"));
        ex.Message.ShouldContain("field");
    }

    [Fact]
    public void Rejects_a_bare_enum_literal_without_a_sink()
    {
        // A bare `.member` with no known result type can't pick an enum — real zig needs the
        // result type. dotcc rejects rather than miscompiling (an untyped `const`).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Color = enum { red, green };\n" +
            "pub fn main() u8 { const c = .red; _ = c; return 0; }\n"));
        ex.Message.ShouldContain("result type");
    }

    [Fact]
    public void Lowers_a_value_receiver_method_as_a_mangled_free_function()
    {
        // A method in a struct body (Milestone D2) lowers to a free function `TypeName_method`
        // with the receiver as its ordinary first parameter; `self.x` is plain field access.
        // A UFCS instance call `p.sum()` rewrites to `Point_sum(p)` (value receiver → passed by
        // value, no `&`).
        var cs = EmitZig(
            "const Point = struct {\n" +
            "    x: i32,\n" +
            "    y: i32,\n" +
            "    fn sum(self: Point) i32 { return self.x + self.y; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point{ .x = 40, .y = 2 };\n" +
            "    return @as(u8, p.sum());\n" +
            "}\n");
        cs.ShouldContain("Point_sum(Point self)");   // method → mangled free function
        cs.ShouldContain("self.x + self.y");          // value receiver → no arrow
        cs.ShouldContain("Point_sum(p)");             // UFCS call → receiver passed by value
    }

    [Fact]
    public void Lowers_a_pointer_receiver_method_with_auto_ref_and_arrow()
    {
        // A `*Point` receiver: UFCS auto-takes the address of a value receiver (`p.scale(2)` →
        // `Point_scale(&p, 2)`), and `self.x` on the pointer receiver auto-derefs to `self->x`.
        var cs = EmitZig(
            "const Point = struct {\n" +
            "    x: i32,\n" +
            "    y: i32,\n" +
            "    fn scale(self: *Point, f: i32) void { self.x = self.x * f; self.y = self.y * f; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    var p = Point{ .x = 20, .y = 1 };\n" +
            "    p.scale(2);\n" +
            "    return @as(u8, p.x + p.y);\n" +
            "}\n");
        cs.ShouldContain("Point_scale");      // method → mangled free function
        cs.ShouldContain("self->x");          // pointer receiver → arrow field access
        cs.ShouldContain("Point_scale(&p, 2)");  // UFCS auto-ref of a value receiver
    }

    [Fact]
    public void Lowers_a_static_associated_function_call()
    {
        // `Type.func(args)` — a function whose first parameter is NOT a receiver — is an
        // associated (static) function: the base names the type, so all arguments are explicit
        // and the call rewrites to the mangled free function with no synthesized receiver.
        var cs = EmitZig(
            "const Point = struct {\n" +
            "    x: i32,\n" +
            "    y: i32,\n" +
            "    fn init(x: i32, y: i32) Point { return .{ .x = x, .y = y }; }\n" +
            "    fn sum(self: Point) i32 { return self.x + self.y; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point.init(40, 2);\n" +
            "    return @as(u8, p.sum());\n" +
            "}\n");
        cs.ShouldContain("Point_init(40, 2)");   // static call → no receiver
        cs.ShouldContain("Point_sum(p)");         // instance call → value receiver
    }

    [Fact]
    public void Lowers_an_at_This_receiver_type()
    {
        // `self: @This()` names the receiver as the enclosing container type without repeating
        // its name — resolves to `Vec`, so the method lowers to `Vec_total` and the call binds.
        var cs = EmitZig(
            "const Vec = struct {\n" +
            "    a: i32,\n" +
            "    b: i32,\n" +
            "    fn total(self: @This()) i32 { return self.a + self.b; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const v = Vec{ .a = 30, .b = 12 };\n" +
            "    return @as(u8, v.total());\n" +
            "}\n");
        cs.ShouldContain("Vec_total(Vec self)");   // @This() resolved to Vec
        cs.ShouldContain("Vec_total(v)");
    }

    [Fact]
    public void Lowers_a_const_Self_at_This_alias()
    {
        // `const Self = @This();` (the ubiquitous Zig idiom) — a container-level const that aliases
        // the container's own type inside its methods. It resolves everywhere the explicit name
        // would: as a parameter type (`self: Self`), a return type (`Self`), the base of a static
        // call (`Self.init(…)`), and a typed literal (`Self{…}`) — all to `Vec`, so the methods
        // lower to `Vec_init` / `Vec_sum` and the literal to `new Vec{…}`.
        var cs = EmitZig(
            "const Vec = struct {\n" +
            "    a: i32,\n" +
            "    b: i32,\n" +
            "    const Self = @This();\n" +
            "    fn init(a: i32, b: i32) Self { return Self{ .a = a, .b = b }; }\n" +
            "    fn sum(self: Self) i32 { return self.a + self.b; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const v = Vec.init(40, 2);\n" +
            "    return @as(u8, v.sum());\n" +
            "}\n");
        cs.ShouldContain("Vec Vec_init(int a, int b)");   // `Self` return type → Vec
        cs.ShouldContain("Vec_sum(Vec self)");             // `self: Self` param → Vec
        cs.ShouldContain("new Vec");                       // `Self{…}` literal → new Vec{…}
        cs.ShouldContain("Vec_init(40, 2)");               // `Self.init(…)` static call via the alias
        cs.ShouldContain("Vec_sum(v)");                    // instance call
    }

    [Fact]
    public void Lowers_namespaced_value_consts()
    {
        // A container-level `const NAME = expr;` is a comptime constant accessed as `Type.NAME`
        // (D2/D3 leftover). dotcc inlines the RHS at each use site (no global storage). Works across
        // struct/enum/union; here a struct const (a typed literal) and an enum const whose value is
        // one of the enum's own members.
        var cs = EmitZig(
            "const Cfg = struct {\n" +
            "    pub const max: u8 = 42;\n" +
            "};\n" +
            "const Color = enum(u8) {\n" +
            "    red, green, blue,\n" +
            "    pub const fallback = Color.blue;\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const d: Color = Color.fallback;\n" +
            "    if (@intFromEnum(d) == 2) { return Cfg.max; }\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("Color.blue");        // `Color.fallback` inlines to the enum constant
        cs.ShouldNotContain("Cfg.max");        // `Cfg.max` inlines to its value (no member access survives)
    }

    [Fact]
    public void Rejects_a_container_var_member()
    {
        // A container-level `var` is a namespaced mutable GLOBAL — it needs real top-level global
        // storage, which the Zig front-end doesn't lower yet. It fails loudly rather than silently
        // dropping the declaration. (A `const` value member IS supported — see above.)
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Counter = struct {\n" +
            "    n: i32,\n" +
            "    var total: i32 = 0;\n" +
            "    fn get(self: Counter) i32 { return self.n; }\n" +
            "};\n" +
            "pub fn main() u8 { const c = Counter{ .n = 42 }; return @as(u8, c.get()); }\n"));
        ex.Message.ShouldContain("total");
        ex.Message.ShouldContain("mutable global");
    }

    [Fact]
    public void Lowers_enum_methods_and_self_equality()
    {
        // An enum body can hold methods (D2/D3 leftover): a `fn`/`pub fn` lowers to a mangled free
        // function `Color_method` with the enum value as its receiver. `self == .red` result-locates
        // the bare `.member` against the receiver's enum type. Both an instance method (`c.isRed()`)
        // and a static associated function (`Color.count()`, no receiver) dispatch through `_methods`.
        var cs = EmitZig(
            "const Color = enum(u8) {\n" +
            "    red,\n" +
            "    green,\n" +
            "    fn isRed(self: Color) bool { return self == .red; }\n" +
            "    fn count() u8 { return 2; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const c: Color = .green;\n" +
            "    if (c.isRed()) { return 1; }\n" +
            "    return Color.count() * 21;\n" +
            "}\n");
        cs.ShouldContain("Color_isRed(Color self)");   // enum method → mangled free fn, enum receiver
        cs.ShouldContain("Color.red");                  // `self == .red` → the `.red` enum constant
        cs.ShouldContain("Color_isRed(c)");             // UFCS instance call → enum value receiver
        cs.ShouldContain("Color_count()");              // static associated function → no receiver
    }

    [Fact]
    public void Lowers_a_union_method()
    {
        // A `union(enum)` body can hold methods (D2/D3 leftover): `fn first(self: Shape) u8` lowers
        // to a mangled free function `Shape_first(Shape self)`, and a direct payload read inside the
        // method (`self.circle`) goes through the nested-union accessor `self.__payload.circle`. The
        // UFCS call `s.first()` passes the union value.
        var cs = EmitZig(
            "const Shape = union(enum) {\n" +
            "    circle: u8,\n" +
            "    square: u8,\n" +
            "    fn first(self: Shape) u8 { return self.circle; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const s = Shape{ .circle = 42 };\n" +
            "    return s.first();\n" +
            "}\n");
        cs.ShouldContain("Shape_first(Shape self)");   // union method → mangled free fn, union receiver
        cs.ShouldContain("__payload.circle");           // payload read inside the method
        cs.ShouldContain("Shape_first(s)");             // UFCS instance call
    }

    [Fact]
    public void Lowers_a_tagged_union_decl_and_payload_construction()
    {
        // `union(enum)` (Milestone D3) → a synthesized tag enum `Shape_Tag` + a discriminated
        // struct `Shape` with a `__tag` field and one field per payload variant. A payload
        // literal `.{ .circle = … }` sets BOTH the tag and the variant's field.
        var cs = EmitZig(
            "const Shape = union(enum) { circle: i32, square: i32, none };\n" +
            "pub fn main() u8 {\n" +
            "    const s: Shape = .{ .circle = 5 };\n" +
            "    _ = s;\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("enum Shape_Tag");        // synthesized tag enum
        cs.ShouldContain("__tag");                  // discriminant field
        cs.ShouldContain("Shape_Tag.circle");       // construction sets the tag…
        cs.ShouldContain("circle = 5");             // …and the payload field
    }

    [Fact]
    public void Lowers_a_void_variant_via_a_bare_dotted_literal()
    {
        // A bare `.none` at a tagged-union sink constructs the void variant — only the tag is
        // set (no payload field).
        var cs = EmitZig(
            "const Shape = union(enum) { circle: i32, none };\n" +
            "pub fn main() u8 {\n" +
            "    const s: Shape = .none;\n" +
            "    _ = s;\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("__tag = Shape_Tag.none");
    }

    [Fact]
    public void Lowers_a_switch_on_a_tagged_union_with_payload_capture()
    {
        // `switch (s) { .circle => |r| … }` on a tagged union → a switch on the `__tag`
        // discriminant; the `|r|` capture binds to the matched variant's overlaid payload field
        // (`s.__payload.circle`, by value) at the top of the prong. The subject `s` is a parameter
        // (a bare var) so it is re-referenced directly — no temp.
        var cs = EmitZig(
            "const Shape = union(enum) { circle: i32, square: i32, none };\n" +
            "fn area(s: Shape) i32 {\n" +
            "    switch (s) {\n" +
            "        .circle => |r| { return r * 2; },\n" +
            "        .square => |x| { return x * x; },\n" +
            "        .none => { return 0; },\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 { const s: Shape = .{ .circle = 5 }; return @as(u8, area(s)); }\n");
        cs.ShouldContain("__tag");                 // switch on the discriminant
        cs.ShouldContain("s.__payload.circle");     // capture reads the overlaid payload field
        cs.ShouldContain("r * 2");                  // capture used in the prong body
    }

    [Fact]
    public void Rejects_a_payload_capture_on_a_non_union_switch()
    {
        // A `|x|` capture is only meaningful on a tagged-union switch; on an integer switch it is
        // rejected (the grammar parses it, the lowering fails loudly).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const x: u8 = 1;\n" +
            "    switch (x) { 1 => |y| { return y; }, else => { return 0; } }\n" +
            "}\n"));
        ex.Message.ShouldContain("tagged-union");
    }

    // ---- slices (Milestone E, stage 1) -----------------------------------

    [Fact]
    public void Lowers_const_slice_param_and_len()
    {
        // `[]const u8` → the runtime ConstSlice<byte> fat pointer; `.len` reads `.Len`.
        var cs = EmitZig("fn lenOf(s: []const u8) usize { return s.len; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("lenOf(ConstSlice<byte> s)");
        cs.ShouldContain("s.Len");
    }

    [Fact]
    public void Lowers_mutable_slice_type()
    {
        // `[]u8` (no const) → the mutable Slice<byte>.
        var cs = EmitZig("fn f(s: []u8) u8 { return s[0]; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("f(Slice<byte> s)");
    }

    [Fact]
    public void Lowers_slice_index_through_the_data_pointer()
    {
        // `s[i]` indexes through the fat pointer's data pointer → `s.Ptr[i]`.
        var cs = EmitZig("fn firstByte(s: []const u8) u8 { return s[0]; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("s.Ptr[0]");
    }

    [Fact]
    public void Lowers_slice_ptr_field()
    {
        // `.ptr` reads the fat pointer's `.Ptr`.
        var cs = EmitZig("fn p(s: []const u8) [*c]const u8 { return s.ptr; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("s.Ptr");
    }

    [Fact]
    public void Coerces_string_literal_to_const_slice_dropping_the_sentinel()
    {
        // A string literal `*const [N:0]u8` → `[]const u8`: the data pointer is the pooled
        // `L("…\0"u8)` and the slice `.len` excludes the trailing NUL (5 for "hello", not 6).
        var cs = EmitZig("pub fn main() u8 { const s: []const u8 = \"hello\"; return s[0]; }\n");
        cs.ShouldContain("new ConstSlice<byte>(");
        cs.ShouldContain("\"hello\\0\"u8");
        cs.ShouldContain(", 5UL)");   // length = 5 (NUL dropped)
    }

    [Fact]
    public void Lowers_slice_range_to_a_sub_slice()
    {
        // `s[a..b]` → new ConstSlice<byte>(s.Ptr + a, (ulong)(b - a)); the length is an
        // explicit `(ulong)` cast so variable (non-constant) bounds convert to the ctor arg.
        var cs = EmitZig("fn mid(s: []const u8, a: usize, b: usize) usize { const m = s[a..b]; return m.len; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("new ConstSlice<byte>(s.Ptr + a");
        cs.ShouldContain("(ulong)(b - a)");
    }

    [Fact]
    public void Lowers_for_over_slice()
    {
        // `for (s) |b| {...}` → for (ulong __i = 0; __i < s.Len; __i++) { byte b = s.Ptr[__i]; ... }
        var cs = EmitZig("fn f(s: []const u8) u8 { var n: u8 = 0; for (s) |b| { n = b; } return n; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("__i < s.Len");
        cs.ShouldContain("byte b = s.Ptr[__i]");
    }

    [Fact]
    public void Lowers_for_over_slice_with_index_capture()
    {
        // `for (s, 0..) |b, i| {...}` also binds the usize index (counter + start).
        var cs = EmitZig("fn f(s: []const u8) usize { var acc: usize = 0; for (s, 0..) |b, i| { acc = acc + i; } return acc; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("s.Ptr[__i]");
        cs.ShouldContain("ulong i = __i + (ulong)0");
    }

    [Fact]
    public void Lowers_fixed_array_local_to_stackalloc_and_slices_it()
    {
        // `var b: [N]T = undefined;` → a stackalloc'd C array (zero heap); slicing it
        // (`b[lo..hi]`) yields a Slice over the stack buffer — the idiomatic stack-backed slice.
        var cs = EmitZig("pub fn main() u8 { var b: [4]u8 = undefined; b[0] = 1; const s: []u8 = b[0..2]; return s[0]; }\n");
        cs.ShouldContain("byte* b = stackalloc byte[4]");
        cs.ShouldContain("new Slice<byte>(b + 0");
    }

    [Fact]
    public void Lowers_scalar_undefined_to_default()
    {
        // `var x: T = undefined;` (scalar) → `default(T)` (a zeroed over-approximation of
        // Zig's uninitialized storage).
        var cs = EmitZig("pub fn main() u8 { var x: u8 = undefined; x = 42; return x; }\n");
        cs.ShouldContain("byte x = default");
    }

    // ---- allocators (Milestone F) ----------------------------------------

    [Fact]
    public void Devirtualizes_the_default_allocator_to_a_direct_c_heap_call()
    {
        // `const a = std.heap.page_allocator; a.alloc(u8, n) / a.free(s)` — the statically-known
        // default DEVIRTUALIZES to a direct ZigAlloc.AllocCHeap / FreeCHeap (a Libc.malloc/free,
        // no vtable). `@import("std")` and the `const a = …` binding are comptime (no decl).
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 4);\n" +
            "    buf[0] = 42;\n" +
            "    const r = buf[0];\n" +
            "    a.free(buf);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("ZigAlloc.AllocCHeap<byte>(4");   // the devirt'd alloc (direct malloc)
        cs.ShouldContain("ZigAlloc.FreeCHeap<byte>(buf)");  // the devirt'd free (direct free)
        cs.ShouldNotContain(".Alloc<byte>(");               // NOT the indirect vtable dispatch
        cs.ShouldNotContain("Allocator a =");               // the comptime binding emits no decl
    }

    [Fact]
    public void Lowers_a_FixedBufferAllocator_through_the_indirect_vtable()
    {
        // A FixedBufferAllocator (the 2nd allocator) over a stack buffer; `fba.allocator()`
        // yields a runtime Allocator (opaque) → `.alloc` dispatches INDIRECTLY through the vtable.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const a = fba.allocator();\n" +
            "    const s = try a.alloc(u8, 3);\n" +
            "    s[0] = 42;\n" +
            "    return s[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("FixedBufferAllocator.Init((byte*)");   // init over the stack buffer
        cs.ShouldContain("ZigAlloc.FbaAllocator(&fba)");          // the allocator() fat pointer
        cs.ShouldContain("Allocator a =");                        // a runtime Allocator local
        cs.ShouldContain(".Alloc<byte>(3");                       // INDIRECT vtable dispatch
        cs.ShouldNotContain("ZigAlloc.AllocCHeap<byte>(");        // NOT devirt'd (the call site; the
                                                                  // runtime's <T> definition doesn't match)
    }

    [Fact]
    public void Passes_an_opaque_allocator_param_and_materializes_the_default()
    {
        // An opaque `std.mem.Allocator` parameter → an `Allocator` C# param, dispatched
        // indirectly. The statically-known default passed to it MATERIALIZES `ZigAlloc.CHeap()`.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn fill(a: std.mem.Allocator, n: usize) ![]u8 {\n" +
            "    const s = try a.alloc(u8, n);\n" +
            "    return s;\n" +
            "}\n" +
            "fn run() !u8 {\n" +
            "    const s = try fill(std.heap.page_allocator, 2);\n" +
            "    return s[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("fill(Allocator a, ulong n)");   // the opaque allocator parameter
        cs.ShouldContain("a.Alloc<byte>(n");               // indirect dispatch on the param
        cs.ShouldContain("fill(ZigAlloc.CHeap()");         // the default materialized as an arg
    }

    [Fact]
    public void Rejects_a_deferred_allocator_create()
    {
        // `a.create(T)` (single-object alloc) returns `Error!*T` — an error-union-over-pointer,
        // which the runtime can't express yet — so it's a clear deferred error, not a miscompile.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const p = try a.create(u8);\n" +
            "    return p.*;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n"));
        ex.Message.ShouldContain("deferred");
    }

    [Fact]
    public void Rejects_an_unmodeled_std_import()
    {
        // `std` is a known-paths resolver, not a real std model — a non-`std` import errors.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const builtin = @import(\"builtin\");\n" +
            "pub fn main() u8 { return 0; }\n"));
        ex.Message.ShouldContain("not modeled");
    }

    [Fact]
    public void Lowers_a_tuple_return_and_destructures_it()
    {
        // The headline use (Milestone G): a function returns a tuple `struct { u8, u8 }` and the
        // caller destructures it with `const a, const b = mm();`. The tuple type → C#
        // `System.ValueTuple<byte, byte>`; the positional `.{ 3, 7 }` at that sink →
        // `new System.ValueTuple<byte, byte>(3, 7)`; the destructure single-evals into a temp,
        // then binds each name to its positional element (`.Item1`/`.Item2`).
        var cs = EmitZig(
            "fn mm() struct { u8, u8 } { return .{ 3, 7 }; }\n" +
            "pub fn main() u8 { const a, const b = mm(); return a + b; }\n");
        cs.ShouldContain("System.ValueTuple<byte, byte> mm()");        // tuple TYPE return
        cs.ShouldContain("new System.ValueTuple<byte, byte>(3, 7)");   // positional literal at the tuple sink
        cs.ShouldContain("__tup");                                      // single-eval temp
        cs.ShouldContain(".Item1");                                     // binder a ← element 0
        cs.ShouldContain(".Item2");                                     // binder b ← element 1
    }

    [Fact]
    public void Destructures_an_inline_tuple_literal()
    {
        // `const a, const b = .{ … };` — the RHS is an inline positional literal with no sink, so
        // its tuple type is INFERRED from the elements, then destructured the same way.
        var cs = EmitZig(
            "pub fn main() u8 { const a, const b = .{ @as(u8, 4), @as(u8, 6) }; return a + b; }\n");
        cs.ShouldContain("new System.ValueTuple<byte, byte>(");   // inferred tuple from the literal
        cs.ShouldContain(".Item1");
        cs.ShouldContain(".Item2");
    }

    [Fact]
    public void Indexes_a_tuple_value()
    {
        // A literal tuple index `t[N]` reads the Nth element (zero-based) → ValueTuple's 1-based
        // `.ItemN+1`. No array indexing is emitted (a tuple field is statically named).
        var cs = EmitZig(
            "pub fn main() u8 { const t = .{ @as(u8, 3), @as(u8, 7) }; return t[0] + t[1]; }\n");
        cs.ShouldContain(".Item1");   // t[0]
        cs.ShouldContain(".Item2");   // t[1]
    }

    [Fact]
    public void Rejects_a_literal_mixing_positional_and_named_fields()
    {
        // A Zig `.{…}` is a tuple (all-positional) OR a struct (all-named), never a mix. dotcc
        // rejects the mix loudly rather than guessing.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 { const t = .{ 1, .y = 2 }; _ = t; return 0; }\n"));
        ex.Message.ShouldContain("mixes positional and named");
    }

    [Fact]
    public void Rejects_a_destructure_arity_mismatch()
    {
        // The binder count must match the tuple's arity — binding 3 names from a 2-tuple errors.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn mm() struct { u8, u8 } { return .{ 3, 7 }; }\n" +
            "pub fn main() u8 { const a, const b, const c = mm(); return a; }\n"));
        ex.Message.ShouldContain("binds 3");
    }

    [Fact]
    public void Rejects_the_deferred_assign_to_existing_destructure()
    {
        // V1 binders are `const`/`var` only — the assign-to-existing-lvalue form (`a, b = e;`) is
        // deferred and structurally excluded at the grammar level (a parse error, not a lowering
        // one), which keeps the `=` lookahead clean vs an ordinary assignment.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 { var a: u8 = 0; var b: u8 = 0; a, b = .{ @as(u8, 1), @as(u8, 9) }; " +
            "_ = &a; _ = &b; return a + b; }\n"));
        ex.Message.ShouldContain("parse");
    }

    [Fact]
    public void Defer_lowers_to_nested_try_finally_in_lifo_order()
    {
        // Two `defer`s + a body statement → nested try/finally. The LAST-declared defer is the
        // INNERMOST try, so its cleanup runs FIRST (Zig's LIFO) — in the emitted text the inner
        // finally (putchar(98)) precedes the outer one (putchar(97)).
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 { defer _ = putchar(97); defer _ = putchar(98); _ = putchar(99); return 0; }\n");
        cs.ShouldContain("try");
        cs.ShouldContain("finally");
        cs.IndexOf("putchar(98)").ShouldBeLessThan(cs.IndexOf("putchar(97)"));   // LIFO nesting
    }

    [Fact]
    public void Errdefer_lowers_to_a_try_catch_that_rethrows()
    {
        // `errdefer` runs only on the error exit → a catch on the propagating ZigErrorReturn that
        // runs the cleanup then re-throws (so the error keeps propagating to the `!T` boundary).
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn f() !u8 { errdefer _ = putchar(69); return 7; }\n" +
            "pub fn main() u8 { return f() catch 1; }\n");
        cs.ShouldContain("catch (ZigErrorReturn)");
        cs.ShouldContain("putchar(69)");
        cs.ShouldContain("throw;");
    }

    [Fact]
    public void Return_error_under_an_errdefer_throws_to_propagate()
    {
        // With an `errdefer` in scope, `return error.X` can't be a direct Err return (a C# catch
        // wouldn't see it) — it's routed through a thrown ZigErrorReturn so the errdefer catch fires.
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "fn f() !u8 { errdefer _ = putchar(69); return error.Boom; }\n" +
            "pub fn main() u8 { return f() catch 1; }\n");
        // The `(ushort)` cast distinguishes the ZigErrorThrow node from the runtime `ErrUnion.Try`
        // helper (which also throws ZigErrorReturn, but as `ZigErrorReturn(u.Code)`).
        cs.ShouldContain("throw new ZigErrorReturn((ushort)");
    }

    [Fact]
    public void Return_error_without_an_errdefer_stays_a_direct_err_return()
    {
        // The throw-rewrite is gated on an in-scope `errdefer`: a function with none keeps the
        // elegant, exception-free direct `Err` return (the throw form must NOT appear).
        var cs = EmitZig(
            "fn f() !u8 { return error.Boom; }\n" +
            "pub fn main() u8 { return f() catch 1; }\n");
        cs.ShouldContain(".Err(");
        // No ZigErrorThrow node (the `(ushort)`-cast throw); the runtime `ErrUnion.Try` helper's
        // own `throw new ZigErrorReturn(u.Code)` is exempt — it carries no `(ushort)` cast.
        cs.ShouldNotContain("throw new ZigErrorReturn((ushort)");
    }

    [Fact]
    public void Lowers_true_and_false_to_csharp_bool_literals()
    {
        // `true`/`false` are bool values → C# `true`/`false`; a `: bool` decl lowers to the
        // store-normalising CBool (which takes a C# bool).
        var cs = EmitZig(
            "pub fn main() u8 { const a: bool = true; const b = false; if (a) { if (b) return 0; return 1; } return 2; }\n");
        cs.ShouldContain("CBool a = true;");
        cs.ShouldContain("= false;");
    }

    [Fact]
    public void Lowers_a_char_literal_to_its_codepoint()
    {
        // A char literal is Zig's comptime_int = the codepoint → an integer literal (`'A'` → 65).
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 { _ = putchar('A'); return 0; }\n");
        cs.ShouldContain("putchar(65)");
    }

    [Fact]
    public void Decodes_char_literal_escapes()
    {
        // Named, hex, and quote/backslash escapes all decode to their byte value via the shared
        // escape machinery: '\n'→10, '\x2A'→42, '\''→39, '\\'→92.
        var cs = EmitZig(
            "pub fn main() u8 { const nl: u8 = '\\n'; const hx: u8 = '\\x2A'; const q: u8 = '\\''; " +
            "const bs: u8 = '\\\\'; return nl + hx + q + bs; }\n");
        cs.ShouldContain("byte nl = 10;");
        cs.ShouldContain("byte hx = 42;");
        cs.ShouldContain("byte q = 39;");
        cs.ShouldContain("byte bs = 92;");
    }

    [Fact]
    public void Lowers_plus_equals_to_a_native_compound_assignment()
    {
        // `x += 5` lowers to the shared Assign with CompoundOp=Add → a NATIVE C# `x += …`,
        // NOT a `x = x + 5` desugar (which would double-evaluate a side-effecting lvalue).
        var cs = EmitZig("pub fn main() u8 { var x: u8 = 37; x += 5; return x; }\n");
        cs.ShouldContain("x +=");
        cs.ShouldNotContain("x = x +");
    }

    [Fact]
    public void Lowers_all_compound_assignment_operators()
    {
        // Each of the 10 operators maps to the same C# operator as the matching Zig binary op.
        // One mutation per line; each compound form is checked in isolation on the same lvalue.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var a: u32 = 100;\n" +
            "    a += 1; a -= 2; a *= 3; a /= 4; a %= 5;\n" +
            "    a <<= 1; a >>= 1; a &= 6; a |= 7; a ^= 8;\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("a +=");
        cs.ShouldContain("a -=");
        cs.ShouldContain("a *=");
        cs.ShouldContain("a /=");
        cs.ShouldContain("a %=");
        cs.ShouldContain("a <<=");
        cs.ShouldContain("a >>=");
        cs.ShouldContain("a &=");
        cs.ShouldContain("a |=");
        cs.ShouldContain("a ^=");
    }

    [Fact]
    public void Compound_assignment_through_a_pointer_keeps_single_evaluation()
    {
        // Binding correctness (the trap): `p.* += 5` renders as a native compound assign on the
        // deref (`*p += …`), NOT `*p = *p + 5`. C#'s `op=` evaluates the target lvalue exactly
        // once — so a side-effecting lvalue (`a[i()] += 1`) is correct without a bound temp.
        var cs = EmitZig(
            "fn bump(p: *u32) void { p.* += 5; }\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("*p +=");
        cs.ShouldNotContain("*p = *p");
    }

    [Fact]
    public void Lowers_a_top_level_const_to_a_global_field()
    {
        // A top-level `const` becomes a `public static` field of DotCcGlobals (the same path the
        // C frontend's file-scope variables take). A typed const keeps its annotation's type; an
        // untyped const infers `int` from its literal (like an untyped local).
        var cs = EmitZig(
            "const PI: f64 = 3.14;\n" +
            "const MAX = 100;\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("public static unsafe double PI = 3.14");
        cs.ShouldContain("public static unsafe int MAX = 100");
    }

    [Fact]
    public void Lowers_a_top_level_var_to_a_mutable_global_field_resolved_by_bare_name()
    {
        // A top-level `var` is a mutable global field; a function body references it by bare name
        // (surfaced through `using static DotCcGlobals;`) and may mutate it.
        var cs = EmitZig(
            "var counter: u8 = 0;\n" +
            "fn bump() void { counter += 1; }\n" +
            "pub fn main() u8 { bump(); return counter; }\n");
        cs.ShouldContain("public static unsafe byte counter = 0");
        cs.ShouldContain("counter +=");
    }

    [Fact]
    public void A_global_initializer_can_reference_an_earlier_global()
    {
        // Globals are lowered in source order, so a later global's initializer resolves an earlier
        // one by bare name (a sibling field of DotCcGlobals; C# field initializers run in
        // declaration order, so the referenced field is set first).
        var cs = EmitZig(
            "const A: u8 = 20;\n" +
            "const B: u8 = A + 22;\n" +
            "pub fn main() u8 { return B; }\n");
        cs.ShouldContain("public static unsafe byte A = 20");
        cs.ShouldContain("byte B = ");
        cs.ShouldContain("A + 22");
    }

    // ---- Milestone I: lexer & literal completeness ----------------------

    [Fact]
    public void Lowers_radix_and_underscored_integer_literals()
    {
        // Zig radix prefixes `0x`/`0o`/`0b` and `_` digit separators — decoded to the same
        // numeric value (the core normalizes to decimal). `0o` octal and `_` separator are
        // Zig-specific (UNLIKE C's bare-`0` octal / `'` separator), handled in DecodeZigInt.
        var cs = EmitZig(
            "const H: u32 = 0xFF;\n" +      // 255
            "const O: u32 = 0o17;\n" +      // 15
            "const B: u32 = 0b1010;\n" +    // 10
            "const U: u32 = 1_000_000;\n" + // 1000000
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("H = 255");
        cs.ShouldContain("O = 15");
        cs.ShouldContain("B = 10");
        cs.ShouldContain("U = 1000000");
    }

    [Fact]
    public void Lowers_hex_and_underscored_float_literals()
    {
        // A hex float `0x1.8p3` (= 1.5 * 2^3 = 12.0) has no C# syntax, so it's converted to a
        // round-trippable decimal; a decimal float keeps `_` separators stripped.
        var cs = EmitZig(
            "const HF: f64 = 0x1.8p3;\n" +
            "const DF: f64 = 1_000.5;\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("HF = 12");       // 0x1.8p3 == 12.0
        cs.ShouldContain("DF = 1000.5");
    }

    [Fact]
    public void Lowers_escaped_quote_and_unicode_escape_in_a_string()
    {
        // `\"` is an escaped quote (the old `"[^"]*"` rule truncated there); `\u{NNNN}` expands to
        // its UTF-8 bytes — U+2764 (❤) = E2 9D A4, which (being > 0x7F) routes to the byte-array path.
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 { _ = printf(\"a\\\"b\"); _ = printf(\"\\u{2764}\"); return 0; }\n");
        cs.ShouldContain("a\\\"b");               // the escaped quote survived into the u8 literal
        cs.ShouldContain("0xE2, 0x9D, 0xA4");     // U+2764 UTF-8 bytes
    }

    [Fact]
    public void Lowers_a_multiline_string_to_a_newline_joined_literal()
    {
        // A run of `\\`-prefixed lines folds into one literal, lines joined by `\n`; escapes are
        // NOT processed inside a multiline string (raw content).
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    _ = printf(\n" +
            "        \\\\line one\n" +
            "        \\\\line two\n" +
            "    );\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("line one\\nline two");   // joined with a `\n` escape in the u8 literal
    }

    [Fact]
    public void Lowers_a_unicode_escape_in_a_char_literal()
    {
        // '\u{41}' is the codepoint 0x41 = 'A' = 65 (a Zig comptime_int).
        var cs = EmitZig("pub fn main() u8 { return '\\u{41}'; }\n");
        cs.ShouldContain("65");
    }

    // ---- Milestone J: result-location cast builtins ----

    [Fact]
    public void Lowers_intCast_and_truncate_to_a_narrowing_cast()
    {
        // @intCast / @truncate infer their target from the SINK (the `u8` binding), not an
        // explicit type arg — both lower to the C `(byte)` cast.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const wide: usize = 20;\n" +
            "    const a: u8 = @intCast(wide);\n" +
            "    const big: u32 = 0x16;\n" +
            "    const b: u8 = @truncate(big);\n" +
            "    return a + b;\n" +
            "}\n");
        cs.ShouldContain("(byte)wide");   // @intCast at a u8 sink
        cs.ShouldContain("(byte)big");    // @truncate at a u8 sink
    }

    [Fact]
    public void Lowers_float_conversion_builtins()
    {
        // @floatFromInt / @floatCast / @intFromFloat take their target from the sink type.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const i: i32 = 42;\n" +
            "    const f: f64 = @floatFromInt(i);\n" +
            "    const g: f32 = @floatCast(f);\n" +
            "    const back: u8 = @intFromFloat(g);\n" +
            "    return back;\n" +
            "}\n");
        cs.ShouldContain("(double)i");   // @floatFromInt at an f64 sink
        cs.ShouldContain("(float)f");    // @floatCast at an f32 sink
        cs.ShouldContain("(byte)g");     // @intFromFloat at a u8 sink
    }

    [Fact]
    public void Lowers_bitCast_to_Unsafe_BitCast()
    {
        // @bitCast is a same-size bit reinterpret (not a value conversion) → Unsafe.BitCast,
        // generic over <source, sink>. The source is the f32 operand, the sink the u32 binding.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const bits: u32 = @bitCast(@as(f32, 1.0));\n" +
            "    const lo: u8 = @truncate(bits);\n" +
            "    return lo;\n" +
            "}\n");
        cs.ShouldContain("System.Runtime.CompilerServices.Unsafe.BitCast<float, uint>");
    }

    [Fact]
    public void Lowers_ptrCast_and_alignCast()
    {
        // @ptrCast reinterprets the pointee type (→ a C pointer cast); @alignCast is the
        // identity in dotcc's managed model (alignment is unobservable), so the nested
        // `@ptrCast(@alignCast(p8))` still lowers to a single `(uint*)` cast.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var x: u32 = 42;\n" +
            "    const p8: *u8 = @ptrCast(&x);\n" +
            "    const p32: *u32 = @ptrCast(@alignCast(p8));\n" +
            "    return @intCast(p32.* & 0xFF);\n" +
            "}\n");
        cs.ShouldContain("(byte*)");   // @ptrCast to *u8
        cs.ShouldContain("(uint*)");   // @ptrCast(@alignCast(...)) to *u32
    }

    [Fact]
    public void Lowers_enumFromInt_to_an_enum_cast()
    {
        // @enumFromInt(int) at an enum sink → a C# cast to the enum type.
        var cs = EmitZig(
            "const Color = enum(u8) { red, green, blue };\n" +
            "pub fn main() u8 {\n" +
            "    const c: Color = @enumFromInt(@as(u8, 2));\n" +
            "    return @intFromEnum(c) + 40;\n" +
            "}\n");
        cs.ShouldContain("(Color)");
    }

    [Fact]
    public void Lowers_sizeOf_to_a_constant_size()
    {
        // @sizeOf(T) reuses the C `sizeof` IR (no sink needed) → C#'s `sizeof(uint)`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const sz: usize = @sizeOf(u32);\n" +
            "    return @intCast(sz * 10 + 2);\n" +
            "}\n");
        cs.ShouldContain("sizeof(uint)");
    }

    [Fact]
    public void Rejects_a_result_location_builtin_with_no_sink()
    {
        // A result-location builtin needs a known target type (Zig requires a result location);
        // an untyped binding gives no sink, so dotcc rejects it with a clear message.
        var ex = Should.Throw<CompileException>(() =>
            EmitZig("pub fn main() u8 { const x = @intCast(5); return x; }\n"));
        ex.Message.ShouldContain("result location");
    }

    // ---- Milestone K: array literals & aggregate globals ----

    [Fact]
    public void Lowers_an_anonymous_array_literal_at_a_sink()
    {
        // `.{a, b, c}` at a `[N]T` sink → a stackalloc'd array (clean ArrayDecl in a decl).
        var cs = EmitZig("pub fn main() u8 { const a: [3]u8 = .{ 10, 11, 12 }; return a[0]; }\n");
        cs.ShouldContain("stackalloc byte[]{ 10, 11, 12 }");
    }

    [Fact]
    public void Lowers_a_typed_array_literal_with_explicit_and_inferred_length()
    {
        // `[N]T{…}` (explicit) and `[_]T{…}` (length inferred from the element count) both lower
        // to a stackalloc'd array.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const b = [3]u8{ 4, 5, 6 };\n" +
            "    const c = [_]u8{ 7, 8 };\n" +
            "    return b[0] + c[0];\n" +
            "}\n");
        cs.ShouldContain("stackalloc byte[]{ 4, 5, 6 }");
        cs.ShouldContain("stackalloc byte[]{ 7, 8 }");
    }

    [Fact]
    public void Rejects_an_array_literal_with_a_count_mismatch()
    {
        // A fixed `[3]u8` sink with two elements is a clear error (Zig requires the count to match).
        var ex = Should.Throw<CompileException>(() =>
            EmitZig("pub fn main() u8 { const a: [3]u8 = .{ 1, 2 }; return a[0]; }\n"));
        ex.Message.ShouldContain("expects 3");
    }

    [Fact]
    public void Lowers_an_array_global_to_a_pinned_store()
    {
        // A `[N]T` array global → a pinned, program-lifetime backing store (not a dangling
        // stackalloc), exposed as a stable `T*`.
        var cs = EmitZig(
            "const table: [3]u8 = .{ 10, 11, 12 };\n" +
            "pub fn main() u8 { return table[0]; }\n");
        cs.ShouldContain("GlobalArrayFrom<byte>(new byte[]{ 10, 11, 12 })");
    }

    [Fact]
    public void Lowers_an_undefined_array_global_to_a_zeroed_store()
    {
        // `var s: [N]T = undefined;` at module scope → a zeroed pinned store.
        var cs = EmitZig(
            "var scratch: [4]u8 = undefined;\n" +
            "pub fn main() u8 { scratch[0] = 42; return scratch[0]; }\n");
        cs.ShouldContain("GlobalArrayZeroed<byte>(4)");
    }

    [Fact]
    public void Lowers_an_aggregate_struct_global()
    {
        // A struct global initializes via the StructInit path → a C# object initializer.
        var cs = EmitZig(
            "const Point = struct { x: u8, y: u8 };\n" +
            "const origin: Point = .{ .x = 6, .y = 7 };\n" +
            "pub fn main() u8 { return origin.x + origin.y; }\n");
        cs.ShouldContain("new Point { x = 6, y = 7 }");
    }

    // ---- Milestone L (part 1): switch as an expression ----

    [Fact]
    public void Lowers_a_switch_expression_over_an_int()
    {
        // `switch (n) { 0,1 => a, 2 => b, else => c }` → a C# switch expression: a multi-value
        // prong joins with `or`, `else` is the `_` default.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const n: u8 = 2;\n" +
            "    const a: u8 = switch (n) { 0, 1 => 5, 2 => 20, else => 0 };\n" +
            "    return a;\n" +
            "}\n");
        cs.ShouldContain("switch {");
        cs.ShouldContain("0 or 1 => 5");
        cs.ShouldContain("_ => 0");
    }

    [Fact]
    public void Lowers_a_switch_expression_over_an_enum_in_return_position()
    {
        // An enum subject decays to its underlying value; `.member` labels decay too, and the
        // whole switch-expression is the returned value.
        var cs = EmitZig(
            "const Color = enum(u8) { red, green, blue };\n" +
            "fn rank(c: Color) u8 { return switch (c) { .red => 10, .green => 20, else => 12 }; }\n" +
            "pub fn main() u8 { return rank(.green); }\n");
        cs.ShouldContain("switch {");
        cs.ShouldContain("=> 20");
        cs.ShouldContain("_ => 12");
    }

    [Fact]
    public void Rejects_a_block_bodied_switch_expression_prong()
    {
        // A block-bodied prong in a switch EXPRESSION needs a labeled `break :blk v` (a later L
        // increment); for now it's a clear error.
        var ex = Should.Throw<CompileException>(() =>
            EmitZig("pub fn main() u8 { const x: u8 = switch (1) { 1 => { return 2; }, else => 0 }; return x; }\n"));
        ex.Message.ShouldContain("yield a value");
    }

    [Fact]
    public void Lowers_a_bare_expr_prong_in_a_statement_switch()
    {
        // The shared bare-expr prong body also serves a STATEMENT switch — `v => expr` becomes an
        // expression statement (here a call), with Zig's no-fall-through `break` appended.
        var cs = EmitZig(
            "extern fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const n: u8 = 1;\n" +
            "    switch (n) { 1 => putchar(42), else => {} }\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("case 1:");
        cs.ShouldContain("putchar(");
    }

    // ---- Milestone L (part 2): labeled block as a value ----

    [Fact]
    public void Lowers_a_typed_decl_labeled_value_block()
    {
        // `const x: T = blk: { …; break :blk v; };` → a result temp default-initialized, the body
        // (with `break :blk v` rewritten to `temp = v; goto end`), an end label, then `x = temp`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const doubled: i32 = blk: {\n" +
            "        const half: i32 = 10;\n" +
            "        break :blk half * 2;\n" +
            "    };\n" +
            "    return @as(u8, @intCast(doubled));\n" +
            "}\n");
        cs.ShouldContain("int __blk0 = default(int);");
        cs.ShouldContain("__blk0 = half * 2;");
        cs.ShouldContain("goto __blk0_end;");
        cs.ShouldContain("__blk0_end:");
        cs.ShouldContain("int doubled = __blk0;");
    }

    [Fact]
    public void Lowers_an_inferred_labeled_value_block()
    {
        // No type annotation — the result temp's type comes from the first `break` value (i32 here).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const tag = blk: {\n" +
            "        const base: i32 = 3;\n" +
            "        break :blk base + 4;\n" +
            "    };\n" +
            "    return @as(u8, @intCast(tag));\n" +
            "}\n");
        cs.ShouldContain("int __blk0 = default(int);");
        cs.ShouldContain("int tag = __blk0;");
    }

    [Fact]
    public void Lowers_a_labeled_value_block_with_a_conditional_break()
    {
        // A `break :blk` inside an `if` must be BRACED (a Block, not a brace-less Seq) so the `goto`
        // stays conditional — `if (c) { temp = v; goto end; }`, never a dangling unconditional goto.
        var cs = EmitZig(
            "fn classify(n: i32) i32 {\n" +
            "    return blk: {\n" +
            "        if (n < 0) break :blk 100;\n" +
            "        break :blk n * 2;\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 { return @as(u8, @intCast(classify(5))); }\n");
        // The conditional break is a braced block, so both the assign and the goto are guarded.
        cs.ShouldContain("__blk0 = 100;");
        cs.ShouldContain("return __blk0;");
    }

    [Fact]
    public void Rejects_a_labeled_value_block_in_an_if_expression_arm()
    {
        // A value-block produces its value via statements, which a C# expression (the ternary an
        // if-expression lowers to) can't host — supported only as a full `=`/`return`/assign RHS.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const x: i32 = if (true) blk: { break :blk 1; } else 0;\n" +
            "    return @as(u8, @intCast(x));\n" +
            "}\n"));
        ex.Message.ShouldContain("labeled value-block");
    }

    [Fact]
    public void Rejects_a_break_to_an_unknown_label()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const x: i32 = blk: { break :nope 1; };\n" +
            "    return @as(u8, @intCast(x));\n" +
            "}\n"));
        ex.Message.ShouldContain("no enclosing labeled block");
    }

    [Fact]
    public void Rejects_a_labeled_value_block_initializing_a_global()
    {
        // A global needs a comptime value; a value-block initializes via runtime statements.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const g: i32 = blk: { break :blk 5; };\n" +
            "pub fn main() u8 { return @as(u8, @intCast(g)); }\n"));
        ex.Message.ShouldContain("comptime value");
    }

    // ---- Milestone L (part 3): labeled loops + labeled break/continue ----

    [Fact]
    public void Lowers_a_labeled_break_to_a_goto_after_the_loop()
    {
        // `break :outer` from a nested loop → `goto __loop0_brk;`, with the break label emitted
        // just AFTER the outer loop. The continue label is NOT emitted (continue :outer is unused).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var hit: i32 = 0;\n" +
            "    var i: i32 = 0;\n" +
            "    outer: while (i < 5) : (i = i + 1) {\n" +
            "        var j: i32 = 0;\n" +
            "        while (j < 5) : (j = j + 1) {\n" +
            "            if (i + j == 3) { hit = 1; break :outer; }\n" +
            "        }\n" +
            "    }\n" +
            "    return @as(u8, @intCast(hit));\n" +
            "}\n");
        cs.ShouldContain("goto __loop0_brk;");
        cs.ShouldContain("__loop0_brk:");
        cs.ShouldNotContain("__loop0_cont");   // continue :outer never used → no continue label
    }

    [Fact]
    public void Lowers_a_labeled_continue_to_a_goto_at_the_body_end()
    {
        // `continue :outer` → `goto __loopN_cont;`, with the continue label at the END of the loop
        // body (so the `: (cont)` step still runs). The break label is NOT emitted (unused).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var n: i32 = 0;\n" +
            "    var a: i32 = 0;\n" +
            "    outer: while (a < 3) : (a = a + 1) {\n" +
            "        var b: i32 = 0;\n" +
            "        while (b < 3) : (b = b + 1) {\n" +
            "            n = n + 1;\n" +
            "            if (b == 1) continue :outer;\n" +
            "        }\n" +
            "        n = n + 100;\n" +
            "    }\n" +
            "    return @as(u8, @intCast(n));\n" +
            "}\n");
        cs.ShouldContain("goto __loop0_cont;");
        cs.ShouldContain("__loop0_cont:");
        cs.ShouldNotContain("__loop0_brk");   // break :outer never used → no break label
    }

    [Fact]
    public void Lowers_a_labeled_for_loop()
    {
        // A `label:` may prefix a `for` loop too (the `LoopStmt` factor covers while + for).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    row: for (0..3) |_| {\n" +   // outer index unused → `|_|` (valid Zig; real zig errors otherwise)
            "        for (0..3) |c| {\n" +
            "            sum = sum + 1;\n" +
            "            if (c == @as(usize, 1)) continue :row;\n" +
            "        }\n" +
            "    }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("goto __loop0_cont;");
        cs.ShouldContain("__loop0_cont:");
    }

    [Fact]
    public void Rejects_a_labeled_break_to_an_unknown_loop()
    {
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    outer: while (true) { break :nope; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("no enclosing labeled loop");
    }

    [Fact]
    public void Rejects_a_labeled_value_break_targeting_a_loop()
    {
        // `break :lbl <value>` where `lbl` names the enclosing LOOP is the labeled-while value form
        // (a loop used as an expression) — deferred. (The loop is a statement, so the break-with-value
        // is reached from inside the loop body, where `lp` is on the labeled-loop stack.)
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    lp: while (true) { break :lp 5; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("labeled-while");
    }

    // ---- Milestone L (part 4): switch ranges ----

    [Fact]
    public void Lowers_a_range_in_a_statement_switch_to_a_relational_pattern()
    {
        // `lo...hi => {…}` → a C# relational-pattern case `case >= lo and <= hi:`. Mixes with a
        // multi-value prong (`100, 200 => …` → `case 100: case 200:`) and `else` (→ `default:`).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var bucket: i32 = 0;\n" +
            "    const n: i32 = 42;\n" +
            "    switch (n) {\n" +
            "        0...9 => { bucket = 1; },\n" +
            "        10...99 => { bucket = 2; },\n" +
            "        100, 200 => { bucket = 3; },\n" +
            "        else => { bucket = 9; },\n" +
            "    }\n" +
            "    return @as(u8, @intCast(bucket));\n" +
            "}\n");
        cs.ShouldContain("case >= 0 and <= 9:");
        cs.ShouldContain("case >= 10 and <= 99:");
        cs.ShouldContain("case 100:");
        cs.ShouldContain("case 200:");
    }

    [Fact]
    public void Lowers_a_range_in_a_switch_expression_to_a_relational_pattern()
    {
        // `lo...hi => e` in a switch EXPRESSION → a relational pattern arm `>= lo and <= hi => e`.
        var cs = EmitZig(
            "fn kind(c: u8) u8 {\n" +
            "    return switch (c) {\n" +
            "        '0'...'9' => 1,\n" +
            "        'A'...'Z' => 2,\n" +
            "        else => 0,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 { return kind('7'); }\n");
        cs.ShouldContain(">= 48 and <= 57 => 1");
        cs.ShouldContain(">= 65 and <= 90 => 2");
        cs.ShouldContain("_ => 0");
    }

    [Fact]
    public void Rejects_a_range_in_a_tagged_union_switch()
    {
        // A union's variants aren't ordered, so `lo...hi` is meaningless there — a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const U = union(enum) { a: i32, b: i32, c: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    const u: U = .{ .a = 1 };\n" +
            "    switch (u) { .a...c => {}, else => {} }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("range");
    }

    [Fact]
    public void Lowers_an_optional_capture_in_if()
    {
        // `if (opt) |x| { … } else { … }` over a value optional `?T` → hoist the condition to a
        // single-eval temp, test `__cap.HasValue`, bind `var x = __cap.Value;` at the top of the
        // then-branch; the else-branch runs on a none.
        var cs = EmitZig(
            "fn pick(p: bool, v: i32) ?i32 { if (p) return v; return null; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (pick(true, 4)) |e| { sum += e; } else { sum += 100; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("int? __cap = pick");
        cs.ShouldContain("Cond.B(__cap.HasValue)");
        cs.ShouldContain("int e = __cap.Value;");
    }

    [Fact]
    public void Lowers_a_discard_capture_in_if_without_binding()
    {
        // `|_|` tests the optional but binds nothing — the then-branch still runs when present, and
        // no payload local is emitted.
        var cs = EmitZig(
            "fn pick(p: bool, v: i32) ?i32 { if (p) return v; return null; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (pick(true, 8)) |_| { sum += 8; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("Cond.B(__cap.HasValue)");
        cs.ShouldNotContain("__cap.Value");   // `_` discards the payload — no binding
    }

    [Fact]
    public void Lowers_an_optional_pointer_capture_in_if()
    {
        // A niche optional pointer `?*T` is a bare `T*`, so the test is a plain non-null check
        // (`Cond.B(void*)`) and the capture binds the unwrapped pointer itself (the same value).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var k: i32 = 0;\n" +
            "    const maybe: ?*i32 = &k;\n" +
            "    if (maybe) |p| { p.* = 42; }\n" +
            "    return @as(u8, @intCast(k));\n" +
            "}\n");
        cs.ShouldContain("if (Cond.B(maybe))");
        cs.ShouldContain("int* p = maybe;");
    }

    [Fact]
    public void Rejects_a_capture_if_on_a_non_optional()
    {
        // `if (n) |x|` where `n` is a plain integer has no payload to bind — a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const n: i32 = 1;\n" +
            "    if (n) |x| { _ = x; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("optional");
    }

    [Fact]
    public void Lowers_an_error_union_capture_in_if()
    {
        // `if (eu) |x| { … } else |e| { … }` → inspect the runtime `ErrUnion<T>`: test `__cap.IsErr`
        // (error branch first, success in the C# `else`, so no `!`), bind `int x = __cap.Value;` on
        // success and `ushort e = __cap.Code;` (the erased error code) on failure.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (mk(true)) |x| { sum += x; } else |e| { _ = e; sum += 100; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("ErrUnion<int> __cap = mk");
        cs.ShouldContain("Cond.B(__cap.IsErr)");
        cs.ShouldContain("ushort e = __cap.Code;");
        cs.ShouldContain("int x = __cap.Value;");
    }

    [Fact]
    public void Lowers_an_error_union_capture_with_a_plain_else()
    {
        // A plain `else` (no `|e|`) discards the error — the error branch runs without binding a code.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (mk(true)) |x| { sum += x; } else { sum += 50; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("Cond.B(__cap.IsErr)");
        cs.ShouldContain("int x = __cap.Value;");
        cs.ShouldNotContain("__cap.Code");   // no `|e|` → the error code is not bound
    }

    [Fact]
    public void Lowers_an_error_union_discard_error_capture()
    {
        // `else |_|` runs the error branch without binding the code.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (mk(false)) |x| { sum += x; } else |_| { sum += 42; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("Cond.B(__cap.IsErr)");
        cs.ShouldNotContain("__cap.Code");   // `_` discards the error — no binding
    }

    [Fact]
    public void Lowers_a_bare_error_value_to_its_code()
    {
        // Milestone N, part 1: a bare `error.Foo` (outside `return error.Foo;`) lowers to its stable
        // code in the flat global error set, typed `CType.ErrorSet` → C# `ushort`. So it can be bound
        // to a const and compared — `e == error.Foo` matches codes (equal codes = equal errors).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const e = error.Foo;\n" +
            "    if (e == error.Foo) { return 42; }\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("ushort e = 1;");   // the sole error name → code 1
        cs.ShouldContain("e == 1");          // error-value equality compares codes
    }

    [Fact]
    public void Distinct_error_values_get_distinct_codes()
    {
        // Two distinct `error.X` names get distinct stable codes (first-encounter order), so a
        // cross-name comparison is a code mismatch.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a = error.First;\n" +
            "    const b = error.Second;\n" +
            "    if (a == b) { return 1; }\n" +
            "    return 42;\n" +
            "}\n");
        cs.ShouldContain("ushort a = 1;");
        cs.ShouldContain("ushort b = 2;");
        cs.ShouldContain("a == b");
    }

    [Fact]
    public void Lowers_a_compared_captured_error()
    {
        // The Milestone-N payoff for the part-3 `else |e|` capture: the bound error is now an
        // error-set value (not the opaque `ushort` code), so `e == error.Bad` compares codes — a
        // USED named capture, valid in both compilers (it was emit-pin-only until error values).
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (mk(false)) |x| { sum += x; } else |e| { if (e == error.Bad) { sum += 42; } }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("ushort e = __cap.Code;");   // captured error (rendered ushort)
        cs.ShouldContain("e == 1");                    // compared against error.Bad's code (1)
    }

    [Fact]
    public void Lowers_a_switch_on_an_error_value()
    {
        // Milestone N, part 2: `switch (e) { error.X => …, else => … }` on an error value lowers to
        // an ORDINARY C# integer switch on the flat code — each `error.X` → a `case <code>:` label,
        // `else` → `default:`. No new lowering over part 1 (the `CType.ErrorSet`-renders-`ushort`
        // marker routes straight through `LowerSwitch`). The error is captured via `else |e|` first.
        var cs = EmitZig(
            "fn classify(n: i32) anyerror!i32 { if (n == 0) return error.Zero; return n; }\n" +
            "pub fn main() u8 {\n" +
            "    var sum: i32 = 0;\n" +
            "    if (classify(0)) |v| { sum += v; } else |e| {\n" +
            "        switch (e) { error.Zero => { sum += 20; }, error.Other => { sum += 5; }, else => { sum += 1; } }\n" +
            "    }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("ushort e = __cap.Code;");   // the switched-on value IS the captured error code
        cs.ShouldContain("switch (e)");
        cs.ShouldContain("case 1:");                   // error.Zero (first error encountered → code 1)
        cs.ShouldContain("case 2:");                   // error.Other → code 2
        cs.ShouldContain("default:");                  // else
    }

    [Fact]
    public void Lowers_a_catch_capture()
    {
        // Milestone N, part 3: `a catch |e| b` binds the error to `e` for the fallback `b`, lowered
        // lazily — hoist the union to `__cE`, bind `ushort e = __cE.Code;`, then a ternary
        // `__cE.IsErr ? b : __cE.Value` so `b` (which may use `e`) runs only on error.
        var cs = EmitZig(
            "fn mayBool(ok: bool) !bool { if (ok) return true; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    const a = mayBool(false) catch |e| (e == error.Bad);\n" +
            "    return if (a) 42 else 0;\n" +
            "}\n");
        cs.ShouldContain("__cE = mayBool");        // the union is hoisted to a single-eval temp
        cs.ShouldContain("ushort e = __cE.Code;"); // the captured error binds to the flat code
        cs.ShouldContain("__cE.IsErr");            // lazy ternary test
        cs.ShouldContain("__cE.Value");            // success arm
        cs.ShouldContain("e == 1");                // the fallback uses `e` (error.Bad → code 1)
    }

    [Fact]
    public void Lowers_a_side_effecting_catch_lazily()
    {
        // A side-effecting (call) fallback can't use the eager `ErrUnion.Catch` (it would run the
        // call unconditionally); it lowers to the lazy ternary so the call happens only on error.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "fn dflt() i32 { return 13; }\n" +
            "pub fn main() u8 {\n" +
            "    const v = mk(false) catch dflt();\n" +
            "    return @as(u8, @intCast(v));\n" +
            "}\n");
        cs.ShouldContain("ErrUnion<int> __cE = mk");      // hoisted union (the lazy path, not eager)
        cs.ShouldContain("(Cond.B(__cE.IsErr) ? dflt()"); // lazy ternary: dflt() only on the error arm
        cs.ShouldContain("__cE.Value");                    // success arm reads the payload
    }

    [Fact]
    public void Keeps_a_simple_catch_eager()
    {
        // A simple, side-effect-free, non-capturing `a catch b` keeps the eager `ErrUnion.Catch`
        // helper (no hoist, no ternary) — unchanged from Milestone B2.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    const v = mk(false) catch 0;\n" +
            "    return @as(u8, @intCast(v));\n" +
            "}\n");
        cs.ShouldContain("ErrUnion.Catch<int>(");   // the eager helper
        cs.ShouldNotContain("__cE");                // no hoist temp
    }

    [Fact]
    public void Rejects_a_catch_capture_in_a_sub_expression()
    {
        // The capture / lazy catch needs a statement context (the bind is a statement); a
        // sub-expression position (here a call argument) is a clear deferred error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn mk(ok: bool) !bool { if (ok) return true; return error.Bad; }\n" +
            "fn id(x: bool) bool { return x; }\n" +
            "pub fn main() u8 {\n" +
            "    const v = id(mk(false) catch |e| (e == error.Bad));\n" +
            "    return if (v) 1 else 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("catch |e|");
    }

    [Fact]
    public void Lowers_an_optional_capture_while()
    {
        // `while (opt) |x| { … }` → `while (true) { var __cap = cond; if (__cap.HasValue) { var x =
        // __cap.Value; … } else break; }` — the condition is re-evaluated (and `__cap` re-bound) each
        // iteration, so it lives inside the loop body.
        var cs = EmitZig(
            "fn nextLT(i: *i32, max: i32) ?i32 { if (i.* >= max) return null; const v = i.*; i.* += 1; return v; }\n" +
            "pub fn main() u8 {\n" +
            "    var i: i32 = 0;\n" +
            "    var sum: i32 = 0;\n" +
            "    while (nextLT(&i, 9)) |v| { sum += v; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("while (Cond.B(true))");
        cs.ShouldContain("int? __cap = nextLT");
        cs.ShouldContain("Cond.B(__cap.HasValue)");
        cs.ShouldContain("int v = __cap.Value;");
    }

    [Fact]
    public void Lowers_a_discard_capture_while_without_binding()
    {
        // `|_|` iterates without binding the payload — no payload local is emitted.
        var cs = EmitZig(
            "fn nextLT(i: *i32, max: i32) ?i32 { if (i.* >= max) return null; const v = i.*; i.* += 1; return v; }\n" +
            "pub fn main() u8 {\n" +
            "    var j: i32 = 0;\n" +
            "    var count: i32 = 0;\n" +
            "    while (nextLT(&j, 6)) |_| { count += 1; }\n" +
            "    return @as(u8, @intCast(count));\n" +
            "}\n");
        cs.ShouldContain("Cond.B(__cap.HasValue)");
        cs.ShouldNotContain("__cap.Value");   // `_` discards the payload — no binding
    }

    [Fact]
    public void Lowers_a_pointer_optional_capture_while()
    {
        // A niche optional pointer `?*T` capture-while tests the pointer for non-null and binds the
        // unwrapped pointer itself; the loop runs until the pointer is set to null.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var x: i32 = 5;\n" +
            "    var p: ?*i32 = &x;\n" +
            "    while (p) |q| { _ = q; p = null; }\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("while (Cond.B(true))");
        cs.ShouldContain("if (Cond.B(__cap))");
        cs.ShouldContain("int* q = __cap;");
    }

    [Fact]
    public void Defers_an_error_union_capture_while()
    {
        // An error-union capture-while is deferred — a clear error (not a silent miscompile).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    while (mk(true)) |x| { _ = x; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("while");
    }

    [Fact]
    public void Rejects_a_capture_while_on_a_non_optional()
    {
        // A plain integer condition has no payload to bind — a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    var n: i32 = 3;\n" +
            "    while (n) |x| { _ = x; n = n - 1; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("optional");
    }

    [Fact]
    public void Lowers_a_by_ref_for_slice_capture()
    {
        // `for (s) |*e| { … }` binds `e` to a `T*` INTO the slice element, so `e.* = …` writes
        // through. → `T* e = &s.Ptr[__i];` (vs the by-value `T e = s.Ptr[__i];`).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var arr = [_]i32{ 1, 2, 3 };\n" +
            "    const s: []i32 = arr[0..3];\n" +
            "    for (s) |*e| { e.* = e.* + 1; }\n" +
            "    return @as(u8, @intCast(s[0]));\n" +
            "}\n");
        cs.ShouldContain("int* e = &s.Ptr[__i];");
        cs.ShouldContain("*e = *e + 1");
    }

    [Fact]
    public void Lowers_a_by_ref_union_switch_capture()
    {
        // `switch (u) { .v => |*p| { … } }` binds `p` to a `T*` INTO the matched variant's payload
        // field, so `p.* = …` writes through to the union. → `T* p = &u.__payload.v;`.
        var cs = EmitZig(
            "const U = union(enum) { n: i32, f: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    var u: U = .{ .n = 0 };\n" +
            "    switch (u) { .n => |*p| { p.* = 42; }, .f => |*p| { p.* = 0; } }\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("int* p = &u.__payload.n;");
        cs.ShouldContain("*p = 42");
    }

    [Fact]
    public void Rejects_a_by_ref_capture_on_a_non_union_switch()
    {
        // A `|*x|` capture is only meaningful on a tagged-union switch (a plain switch has no
        // payload field to point into) — a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const n: i32 = 1;\n" +
            "    switch (n) { 1 => |*x| { _ = x; }, else => {} }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("tagged-union");
    }
}

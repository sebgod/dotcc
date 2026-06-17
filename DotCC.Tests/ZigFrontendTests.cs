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
}

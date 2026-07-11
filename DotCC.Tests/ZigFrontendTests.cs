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
    public void Lowers_i128_and_u128_to_System_Int128_and_UInt128()
    {
        // Milestone ß ("sharp-s"): Zig i128/u128 → C# System.Int128 / System.UInt128 (BCL
        // primitives — arithmetic comes for free; no clean-room type like Float128 needed).
        var cs = EmitZig("fn f(a: i128, b: u128) i128 { return a; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("f(System.Int128 a, System.UInt128 b)");
    }

    [Fact]
    public void Emits_a_128_bit_literal_beyond_ulong_via_Parse()
    {
        // A u128 literal larger than ulong has no C# literal form (no Int128 suffix), so it
        // materializes via Parse. 0xffff_ffff_ffff_ffff_ffff == 2^80-1 == 1208925819614629174706175.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const x: u128 = 0xffff_ffff_ffff_ffff_ffff;\n" +
            "    _ = x;\n" +
            "    return 0;\n}\n");
        cs.ShouldContain("System.UInt128.Parse(\"1208925819614629174706175\")");
    }

    [Fact]
    public void Lowers_wrapping_arithmetic_on_a_128_bit_integer()
    {
        // Wrapping +% on i128/u128 IS supported — native C# Int128/UInt128 wrap under unchecked
        // (no sub-int cast-back at 16 bytes). Only saturating is the cut (below).
        var cs = EmitZig("fn f(a: u128, b: u128) u128 { return a +% b; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("a + b");
    }

    [Fact]
    public void Rejects_saturating_arithmetic_on_a_128_bit_integer()
    {
        // Saturating +|/-|/*| clamps via ZigMath's exact-128-bit accumulator, which a 128-bit
        // operand would itself overflow — a documented V1 cut, rejected with a clear message.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn f(a: u128, b: u128) u128 { return a +| b; }\npub fn main() u8 { return 0; }\n"));
        ex.Message.ShouldContain("128-bit");
    }

    [Fact]
    public void Returns_a_fixed_array_by_value_via_a_heap_owned_copy()
    {
        // The Milestone K "array-by-value return" cut, made sound. A `[N]T`-returning function
        // emits a `T*` signature; returning the stackalloc array local directly would be a
        // dangling pointer once the callee frame pops, but Zig arrays are value types. The return
        // is lowered to a heap-owned copy (ZigAlloc.CopyArrayResult) so the result outlives the
        // call — no spurious cast wraps it (the node carries the array type, so the return
        // coercion is a no-op).
        var cs = EmitZig(
            "fn buildTable() [4]u32 {\n" +
            "    var t: [4]u32 = undefined;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 4) { t[i] = @intCast(i * i); i = i + 1; }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 { const tbl = buildTable(); return @intCast(tbl[3]); }\n");
        cs.ShouldContain("uint* buildTable()");
        cs.ShouldContain("ZigAlloc.CopyArrayResult<uint>(t, 4)");
    }

    [Fact]
    public void Folds_binary_op_enum_initializers_via_the_comptime_interpreter()
    {
        // Milestone T: a Zig enum value may now be any constant expression, not just a
        // literal or a unary of one — the old ZigConstEval rejected `1 << 2`. The shared
        // comptime interpreter folds the shifts (and the `- 1`), so the members land at 1/2/4/7.
        var cs = EmitZig(
            "const Flags = enum(u8) { read = 1 << 0, write = 1 << 1, exec = 1 << 2, all = (1 << 3) - 1 };\n" +
            "pub fn main() u8 { return @intFromEnum(Flags.all); }\n");
        cs.ShouldContain("read = 1,");
        cs.ShouldContain("write = 2,");
        cs.ShouldContain("exec = 4,");
        cs.ShouldContain("all = 7,");
    }

    [Fact]
    public void Folds_a_computed_array_size_via_the_comptime_interpreter()
    {
        // Milestone T: a `[N]T` size may now be a constant expression (`2 * 8`), not only a
        // bare integer literal — ConstEvalArraySize defers the non-literal form to the
        // comptime interpreter, which folds it to 16.
        var cs = EmitZig("pub fn main() u8 { var b: [2 * 8]u8 = undefined; b[0] = 1; return b[0]; }\n");
        cs.ShouldContain("stackalloc byte[16]");
    }

    [Fact]
    public void Folds_a_comptime_expression_prefix_and_splices_the_literal()
    {
        // Milestone T part 2: `comptime EXPR` forces compile-time evaluation of a value and
        // splices the result as a literal. `comptime (2 + 3)` folds to 5 (the trailing `* 4`
        // is outside the prefix, so it stays a runtime `5 * 4`); `comptime @sizeOf(u64)` to 8.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u32 = comptime (2 + 3) * 4;\n" +
            "    const sz: u32 = comptime @sizeOf(u64);\n" +
            "    _ = a; _ = sz;\n" +
            "    return 0;\n}\n");
        cs.ShouldContain("5 * 4");   // comptime (2 + 3) folded to 5
        cs.ShouldContain("8UL");     // comptime @sizeOf(u64) folded to the size_t literal 8
    }

    [Fact]
    public void Evaluates_a_recursive_comptime_function_call()
    {
        // Milestone T part 2b: `comptime fib(10)` interprets the recursive callee in the comptime
        // interpreter (if/return + a call frame per recursion) and splices the result 55 as a
        // literal — no fib call survives at the call site.
        var cs = EmitZig(
            "fn fib(n: u32) u32 { if (n < 2) return n; return fib(n - 1) + fib(n - 2); }\n" +
            "pub fn main() u8 { const f: u32 = comptime fib(10); _ = f; return 0; }\n");
        cs.ShouldContain("55u");
    }

    [Fact]
    public void Evaluates_a_loop_based_comptime_function_call()
    {
        // A comptime function with a local `var`, a `while` loop, and mutation — the statement
        // walker runs the loop and assignments, folding fact(5) to 120.
        var cs = EmitZig(
            "fn fact(n: u32) u32 { var r: u32 = 1; var i: u32 = 2; while (i <= n) { r = r * i; i = i + 1; } return r; }\n" +
            "pub fn main() u8 { const f: u32 = comptime fact(5); _ = f; return 0; }\n");
        cs.ShouldContain("120u");
    }

    [Fact]
    public void Lowers_a_compound_assignment_while_continue_expression()
    {
        // `while (cond) : (i += 1) body` — the canonical Zig loop. The continue-expression
        // now accepts the full assignment-operator set (not just plain `=`), so `i += 1`
        // lowers to the same native compound-assign in the C `for`-post that a statement
        // `i += 1;` produces (see the AssignOp grammar nonterminal + ContAssignPost).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var s: i32 = 0;\n" +
            "    var i: i32 = 0;\n" +
            "    while (i < 5) : (i += 1) { s += i; }\n" +
            "    return @intCast(s);\n" +
            "}\n");
        cs.ShouldContain("for (; Cond.B(");   // the while → for lowering
        cs.ShouldContain("; i += 1)");         // compound-assign continue-expr in the post
    }

    [Fact]
    public void Lowers_a_shift_assign_while_continue_and_still_accepts_plain_assign()
    {
        // A non-additive compound op in the continue (`p <<= 1`) rides the same path; and the
        // legacy plain `=` continue (`i = i + 1`) still lowers exactly as before — the widening
        // is a superset, byte-identical on the `=` form.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var p: i32 = 1;\n" +
            "    while (p < 16) : (p <<= 1) {}\n" +
            "    var i: i32 = 0;\n" +
            "    while (i < 3) : (i = i + 1) {}\n" +
            "    return @intCast(p);\n" +
            "}\n");
        cs.ShouldContain("; p <<= 1)");        // shift-assign continue-expr
        cs.ShouldContain("; i = i + 1)");      // plain-assign continue-expr, unchanged
    }

    [Fact]
    public void Rejects_a_nonterminating_comptime_evaluation()
    {
        // The eval-step budget is the non-termination backstop (Zig's @setEvalBranchQuota): a
        // comptime computation that blows past it is a loud error, never a hang.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn fib(n: u32) u32 { if (n < 2) return n; return fib(n - 1) + fib(n - 2); }\n" +
            "pub fn main() u8 { const x: u32 = comptime fib(40); return @intCast(x); }\n"));
        ex.Message.ShouldContain("budget");
    }

    [Fact]
    public void Evaluates_a_comptime_function_returning_a_struct_value()
    {
        // Milestone T — comptime aggregates: a comptime function returning a STRUCT by value. The
        // interpreter zero-fills `var c: Config = undefined`, applies the field stores, and folds
        // `c.width * c.height`; the result splices back as a `new Config { … }` object initializer
        // in declared field order (so no buildConfig call survives at the use site). A LOCAL
        // `const l = comptime f()` is the round-trippable form — real zig rejects the keyword on a
        // CONTAINER const ("redundant comptime in already-comptime scope"), so the global is left
        // off here (dotcc folds it too, leniently, but it isn't valid zig).
        var cs = EmitZig(
            "const Config = struct { width: u32, height: u32, area: u32 };\n" +
            "fn buildConfig() Config {\n" +
            "    var c: Config = undefined;\n" +
            "    c.width = 4; c.height = 5; c.area = c.width * c.height;\n" +
            "    return c;\n}\n" +
            "pub fn main() u8 { const l = comptime buildConfig(); return @intCast(l.area + 22); }\n");
        cs.ShouldContain("new Config { width = 4u, height = 5u, area = 20u }");
    }

    [Fact]
    public void Evaluates_a_comptime_function_returning_an_array_table()
    {
        // Milestone T — comptime aggregates: a comptime function returning an ARRAY (a lookup table).
        // The interpreter zero-fills `var t: [N]u32 = undefined`, runs the fill loop with `t[i] = …`,
        // and splices the result as a `stackalloc T[]{ … }` at the local use site — no squares() call
        // survives there. (squares() itself returns an array by value, which is sound — see the
        // array-by-value-return increment.) Works for both an inferred and an annotated local type.
        var cs = EmitZig(
            "fn squares() [4]u32 {\n" +
            "    var t: [4]u32 = undefined;\n" +
            "    var i: usize = 0;\n" +
            "    while (i < 4) { t[i] = @intCast(i * i); i = i + 1; }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 { const tbl = comptime squares(); return @intCast(tbl[3]); }\n");
        cs.ShouldContain("stackalloc uint[]{ 0u, 1u, 4u, 9u }");
    }

    [Fact]
    public void Rejects_a_comptime_array_at_a_global_const()
    {
        // A comptime array at a CONTAINER `const` isn't round-trippable (real zig rejects `comptime`
        // on an already-comptime container const) and the global path has no pinned re-home for the
        // resolved StackArray, so it's a clear error — never a silent `static T* = stackalloc …`
        // miscompile. A LOCAL `const x = comptime f();` works; a runtime `const X = f();` (no keyword)
        // works via the sound array-by-value return.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn squares() [4]u32 { var t: [4]u32 = undefined; var i: usize = 0; while (i < 4) { t[i] = @intCast(i * i); i = i + 1; } return t; }\n" +
            "const TBL = comptime squares();\n" +
            "pub fn main() u8 { return @intCast(TBL[3]); }\n"));
        ex.Message.ShouldContain("comptime array at a global");
    }

    [Fact]
    public void Unrolls_an_inline_for_into_per_iteration_blocks()
    {
        // Milestone T, part 3 — `inline for (lo..hi) |i|` UNROLLS at compile time: the body is
        // replicated once per index, each copy binding the capture to that iteration's constant
        // (`ulong i = 0UL;`, `ulong i__1 = 1UL;`, …). No runtime `for` header for this loop survives —
        // the distinct, renamed sibling index locals are the unroll signature (a real loop has one `i`).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (0..3) |i| { sum += @intCast(i); }\n" +
            "    return @intCast(sum + 39);\n}\n");
        cs.ShouldContain("ulong i = 0UL;");
        cs.ShouldContain("ulong i__1 = 1UL;");
        cs.ShouldContain("ulong i__2 = 2UL;");
    }

    [Fact]
    public void Runs_an_inline_for_at_comptime_to_fold_a_table()
    {
        // The same `inline for` runs INSIDE the comptime interpreter when the enclosing function is
        // `comptime`-called: the interpreter walks the unrolled copies (each `const i = v` binds into
        // its frame), fills the array, and the result splices to a `stackalloc` literal — proving the
        // unrolled IR is uniform across the runtime and comptime paths (no `inline for`-specific
        // interpreter case). No buildSquares() call survives at the use site.
        var cs = EmitZig(
            "fn buildSquares() [5]u32 {\n" +
            "    var t: [5]u32 = undefined;\n" +
            "    inline for (0..5) |i| { t[i] = @intCast(i * i); }\n" +
            "    return t;\n}\n" +
            "pub fn main() u8 { const sq = comptime buildSquares(); return @intCast(sq[4] - 14); }\n");
        cs.ShouldContain("stackalloc uint[]{ 0u, 1u, 4u, 9u, 16u }");
    }

    [Fact]
    public void Unrolls_an_inline_while_with_a_comptime_var_counter()
    {
        // Milestone T, part 3 — `inline while (i < N) : (i = i + step)` over a `comptime var` counter
        // unrolls: the counter substitutes to its current value each round (so `arr[i]` becomes
        // `arr[0]`, `arr[1]`, …) and no runtime `while`/counter survives. The substituted index reads
        // are the unroll signature.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const arr = [_]u32{ 5, 10, 15, 20 };\n" +
            "    var sum: u32 = 0;\n" +
            "    comptime var i: usize = 0;\n" +
            "    inline while (i < 4) : (i = i + 1) { sum += arr[i]; }\n" +
            "    return @intCast(sum - 8);\n}\n");
        cs.ShouldContain("arr[0UL]");
        cs.ShouldContain("arr[3UL]");
    }

    [Fact]
    public void Folds_an_inline_while_inside_a_comptime_call()
    {
        // The unrolled `inline while` is plain straight-line IR, so a `comptime`-called function that
        // uses one folds entirely: triangular sums 1..8 (= 36) via an inline while, +6 = 42 → baked in.
        var cs = EmitZig(
            "fn triangular(n: u32) u32 {\n" +
            "    var acc: u32 = 0;\n" +
            "    comptime var i: u32 = 1;\n" +
            "    inline while (i <= 8) : (i = i + 1) { acc += i; }\n" +
            "    return acc + n;\n}\n" +
            "pub fn main() u8 { const t = comptime triangular(6); return @intCast(t); }\n");
        cs.ShouldContain("uint t = 42u");
    }

    [Fact]
    public void Rejects_inline_while_without_a_comptime_var_counter()
    {
        // An `inline while` whose counter is a runtime `var` (not a `comptime var`) can't be unrolled
        // (its value isn't comptime-known) — a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    var i: usize = 0;\n" +
            "    var sum: u32 = 0;\n" +
            "    inline while (i < 3) : (i = i + 1) { sum += 1; }\n" +
            "    return @intCast(sum + 39);\n}\n"));
        ex.Message.ShouldContain("comptime var");
    }

    [Fact]
    public void Rejects_a_bare_inline_while_without_a_continue_expr()
    {
        // Only the continue-expression form `inline while (c) : (i = …)` is unrolled in V1; a bare
        // `inline while (c) body` (counter mutated in the body) is a clear deferred error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    comptime var i: u8 = 0;\n" +
            "    inline while (i < 3) { i = i + 1; }\n" +
            "    return i + 39;\n}\n"));
        ex.Message.ShouldContain("inline while");
    }

    [Fact]
    public void Rejects_break_inside_an_inline_for()
    {
        // A bare `break`/`continue` in an `inline for` body targets the loop, which unrolling removes —
        // a clear deferred error, never a silent C# "break outside loop".
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    var sum: u8 = 0;\n" +
            "    inline for (0..3) |i| { sum += @intCast(i); if (sum > 0) break; }\n" +
            "    return sum + 42;\n}\n"));
        ex.Message.ShouldContain("inline for");
    }

    [Fact]
    public void Rejects_non_constant_inline_for_bounds()
    {
        // The bounds must fold to a compile-time constant — a runtime variable bound is rejected
        // (Zig requires `inline for` bounds to be comptime-known too).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    var n: usize = 3;\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (0..n) |i| { sum += @intCast(i); }\n" +
            "    return @intCast(sum + 42);\n}\n"));
        ex.Message.ShouldContain("compile-time-known");
    }

    [Fact]
    public void Unrolls_an_inline_for_over_a_fixed_array()
    {
        // Milestone T, part 3 — `inline for (arr) |x|` over a fixed `[N]T` array unrolls once per
        // element, each copy binding `x` to that element by value (`x = items[k]`). The distinct
        // element reads (items[0], items[3], …) are the unroll signature.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const items = [_]u32{ 3, 7, 11, 21 };\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (items) |x| { sum += x; }\n" +
            "    return @intCast(sum);\n}\n");
        cs.ShouldContain("items[0UL]");
        cs.ShouldContain("items[3UL]");
    }

    [Fact]
    public void Rejects_inline_for_with_an_index_capture()
    {
        // The indexed `inline for (arr, 0..) |x, i|` and by-ref `|*x|` forms are deferred in V1 —
        // a clear error, not a silent miscompile.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    const items = [_]u32{ 1, 2, 3 };\n" +
            "    var sum: u32 = 0;\n" +
            "    inline for (items, 0..) |x, i| { sum += x + @as(u32, @intCast(i)); }\n" +
            "    return @intCast(sum + 36);\n}\n"));
        ex.Message.ShouldContain("inline");
    }

    [Fact]
    public void Runs_a_comptime_block_at_compile_time()
    {
        // Milestone T, part 3 — a `comptime { … }` block STATEMENT runs at compile time: it folds its
        // comptime statements (here a `while` summing 1..8 = 36 into the enclosing `comptime var
        // total`) and emits NO runtime code. References to `total` afterward substitute the folded
        // value, so `36u` appears with no runtime loop.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    comptime var total: u32 = 0;\n" +
            "    comptime {\n" +
            "        var i: u32 = 1;\n" +
            "        while (i <= 8) : (i = i + 1) { total = total + i; }\n" +
            "    }\n" +
            "    return @intCast(total + 6);\n}\n");
        cs.ShouldContain("36u");
    }

    [Fact]
    public void Rejects_a_comptime_block_storing_to_a_runtime_var()
    {
        // A `comptime { … }` block has no runtime effect, so a store to a runtime `var` (not a
        // `comptime var`) is a clear error — matching real zig, which also forbids it.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    var r: u32 = 0;\n" +
            "    comptime { r = 42; }\n" +
            "    return @intCast(r);\n}\n"));
        ex.Message.ShouldContain("comptime var");
    }

    [Fact]
    public void Folds_alignOf_to_a_literal()
    {
        // Milestone T, part 4 — `@alignOf(T)` is a pure compile-time constant (the ABI alignment via
        // the layout model), so it folds straight to a literal (no IR node). A 16-byte integer aligns
        // to 16.
        var cs = EmitZig("pub fn main() u8 { const a: usize = @alignOf(i128); return @intCast(a + 26); }\n");
        cs.ShouldContain("16UL");
    }

    [Fact]
    public void Folds_offsetOf_in_a_comptime_position()
    {
        // Milestone T, part 4 — `@offsetOf(T, "field")` reuses the C `offsetof` IR and folds via the
        // layout engine in a comptime-required position (an array bound). `b` follows a `u8` then 3
        // pad bytes, so it sits at C-ABI offset 4 → the bound is 4.
        var cs = EmitZig(
            "const Point = struct { a: u8, b: u32, c: u16 };\n" +
            "pub fn main() u8 { var pad: [@offsetOf(Point, \"b\")]u8 = undefined; pad[0] = 42; return pad[0]; }\n");
        cs.ShouldContain("stackalloc byte[4]");
    }

    [Fact]
    public void Rejects_offsetOf_on_a_non_struct_type()
    {
        // `@offsetOf` needs an aggregate as its first argument — a primitive type is a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 { const o: usize = @offsetOf(u32, \"x\"); return @intCast(o); }\n"));
        ex.Message.ShouldContain("struct/union type");
    }

    [Fact]
    public void Folds_fixed_array_len_to_its_count()
    {
        // `arr.len` on a fixed `[N]T` array is the comptime-known count N (Zig), folded to a literal —
        // the array lowered to a pointer (no runtime length field, unlike a slice's `.Len`).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var arr: [7]u32 = undefined;\n" +
            "    arr[0] = 0;\n" +
            "    return @intCast(arr.len + 35);\n}\n");
        cs.ShouldContain("7UL");
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
    public void Lowers_an_error_union_void_main()
    {
        // `pub fn main() !void` (Milestone N, part 4) → main returns `ErrUnion<Unit>`. The entry
        // calls it into a temp; an error maps to a non-zero exit (1, reported to stderr), success to
        // exit 0 (a void payload has no exit value). Matches real zig's `!void` main.
        var cs = EmitZig(
            "fn boom(go: bool) !void { if (go) return error.Bad; }\n" +
            "pub fn main() !void { try boom(false); }\n");
        cs.ShouldContain("ErrUnion<Unit> main()");          // error-union main lowered
        cs.ShouldContain("var __mr = main();");              // entry: call into a temp
        cs.ShouldContain("if (__mr.IsErr)");                 // error → non-zero exit
        cs.ShouldContain("return 1;");                       // error exit code
        cs.ShouldContain("return 0;");                       // success: void payload → exit 0
    }

    [Fact]
    public void Lowers_an_error_union_int_main()
    {
        // `pub fn main() !u8` → main returns `ErrUnion<byte>`. Success exits with the payload value;
        // an error exits 1. Matches real zig's `!u8` main.
        var cs = EmitZig(
            "fn mk(ok: bool) !u8 { if (ok) return 42; return error.Bad; }\n" +
            "pub fn main() !u8 { const v = try mk(true); return v; }\n");
        cs.ShouldContain("ErrUnion<byte> main()");           // error-union main lowered
        cs.ShouldContain("var __mr = main();");              // entry: call into a temp
        cs.ShouldContain("if (__mr.IsErr)");                 // error → non-zero exit
        cs.ShouldContain("return (int)__mr.Value;");         // success: payload → exit code
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
    public void Lowers_pub_wrapped_container_decls()
    {
        // A container decl may be `pub`-wrapped (`pub const P = struct/enum/union {…}`) — `Unwrap`
        // peels the modifier (a no-op in single-module emit) so the container lowers exactly like a
        // bare one. Grouping the container forms under `ContainerDecl` lets one peel cover them all.
        var cs = EmitZig(
            "pub const Point = struct { x: i32, y: i32 };\n" +
            "pub const Kind = enum { a, b, c };\n" +
            "pub const Num = union(enum) { i: i32, f: f32 };\n" +
            "pub fn main() u8 {\n" +
            "    const p = Point{ .x = 10, .y = 20 };\n" +
            "    const k = Kind.b;\n" +
            "    const n = Num{ .i = 12 };\n" +
            "    _ = n;\n" +
            "    return @intCast(p.x + @as(i32, @intFromEnum(k)));\n" +
            "}\n");
        cs.ShouldContain("unsafe struct Point");   // pub struct still emits the struct
        cs.ShouldContain("enum Kind");             // pub enum → real C# enum
        cs.ShouldContain("Num");                   // pub union emitted
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
    public void Lowers_a_container_var_alongside_fields_and_methods()
    {
        // A container-level `var` is a namespaced mutable GLOBAL (Milestone R, part 6) — lowered to a
        // mangled `Counter_total` global field, coexisting with the struct's instance field `n` and a
        // method. (A `const` value member is supported too — see above.)
        var cs = EmitZig(
            "const Counter = struct {\n" +
            "    n: i32,\n" +
            "    var total: i32 = 0;\n" +
            "    fn get(self: Counter) i32 { return self.n; }\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    const c = Counter{ .n = 30 };\n" +
            "    Counter.total = c.get();\n" +
            "    Counter.total += 12;\n" +
            "    return @intCast(Counter.total);\n" +
            "}\n");
        cs.ShouldContain("Counter_total = 0");   // the namespaced var → a mangled global field
        cs.ShouldContain("Counter_total =");     // `Counter.total = …` writes the global
        cs.ShouldContain("int n;");              // the instance field stays a real struct member
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
    public void Lowers_a_union_with_an_explicit_named_tag_enum()
    {
        // Milestone R — `union(Kind)`: the discriminant is an EXISTING named enum, not a synthesized
        // `U_Tag`. The union reuses the tagged-union shape but the `__tag` field is typed by the named
        // enum, and construction + the tag VALUE come from that enum's members (here `Kind` uses 1/2/4,
        // proving the named enum drives the discriminant rather than a 0-based synthesized one).
        var cs = EmitZig(
            "const Kind = enum(u8) { num = 1, small = 2 };\n" +
            "const Value = union(Kind) { num: i32, small: u8 };\n" +
            "pub fn main() u8 {\n" +
            "    const a: Value = .{ .num = 7 };\n" +
            "    _ = a;\n" +
            "    return 0;\n" +
            "}\n");
        cs.ShouldContain("public Kind __tag;");              // discriminant typed by the NAMED enum
        cs.ShouldContain("new Value { __tag = Kind.num");    // construction names the named-enum member
        cs.ShouldNotContain("Value_Tag");                    // no synthesized tag enum
    }

    [Fact]
    public void Rejects_a_named_tag_union_variant_not_in_the_enum()
    {
        // A `union(Kind)` variant must be a member of the tag enum `Kind` — a typo / extra variant
        // is rejected loudly (a structural check, not a silent mis-tag).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Kind = enum { a, b };\n" +
            "const Value = union(Kind) { a: i32, c: u8 };\n" +
            "pub fn main() u8 { return 0; }\n"));
        ex.Message.ShouldContain("not a member of enum 'Kind'");
    }

    [Fact]
    public void Lowers_an_extern_struct_with_sequential_layout()
    {
        // Milestone R, part 2 — `extern struct` pins guaranteed C-ABI layout: dotcc emits
        // [StructLayout(Sequential)] (no packing). Field access lowers like any struct.
        var cs = EmitZig(
            "const P = extern struct { a: u8, b: u32 };\n" +
            "pub fn main() u8 {\n" +
            "    var p: P = .{ .a = 1, .b = 2 };\n" +
            "    p.b += 3;\n" +
            "    return @intCast(p.b);\n" +
            "}\n");
        cs.ShouldContain("LayoutKind.Sequential)]");   // explicit C-ABI layout
        cs.ShouldContain("unsafe struct P");
        cs.ShouldNotContain("Pack = 1");               // extern is NOT packed
    }

    [Fact]
    public void Lowers_a_packed_struct_with_pack1_layout()
    {
        // Milestone R, part 2 — `packed struct` removes inter-field padding: dotcc emits
        // [StructLayout(Sequential, Pack=1)] (V1 byte-packs, not bit-packs).
        var cs = EmitZig(
            "const P = packed struct { a: u8, b: u8, c: u8, d: u8 };\n" +
            "pub fn main() u8 {\n" +
            "    var p: P = .{ .a = 1, .b = 2, .c = 3, .d = 4 };\n" +
            "    return @intCast(@as(u32, p.a) + p.b + p.c + p.d);\n" +
            "}\n");
        cs.ShouldContain("LayoutKind.Sequential, Pack = 1)]");
        cs.ShouldContain("unsafe struct P");
    }

    [Fact]
    public void Lowers_an_untagged_union_as_an_overlay_struct()
    {
        // Milestone R, part 3 — `union { … }` (no tag) → a bare [StructLayout(Explicit)] overlay
        // struct (every variant at FieldOffset(0)), NOT a ZigUnionInfo: NO outer __tag/__payload.
        // Construction + access route through the ordinary struct-init / member paths.
        var cs = EmitZig(
            "const Box = union { small: u8, big: u32 };\n" +
            "pub fn main() u8 {\n" +
            "    var a: Box = .{ .small = 10 };\n" +
            "    a.small += 5;\n" +
            "    return @intCast(a.small);\n" +
            "}\n");
        cs.ShouldContain("LayoutKind.Explicit)]");      // overlapping storage
        cs.ShouldContain("unsafe struct Box");
        cs.ShouldContain("new Box { small = 10 }");     // ordinary struct construction
        cs.ShouldNotContain("__tag");                   // no discriminant
        cs.ShouldNotContain("Box_Payload");             // no nested payload union (it IS the union)
    }

    [Fact]
    public void Rejects_a_void_variant_in_an_untagged_union()
    {
        // An untagged union has no tag to select a void variant — require a payload type (a void
        // variant needs a tagged `union(enum)`).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Bad = union { a: u8, b };\n" +
            "pub fn main() u8 { return 0; }\n"));
        ex.Message.ShouldContain("must have a type");
    }

    [Fact]
    public void Lowers_export_and_pub_export_functions_like_ordinary_ones()
    {
        // Milestone R, part 4 — `export fn` / `pub export fn` force C-ABI external linkage in Zig;
        // dotcc peels the modifier (Unwrap) and lowers them as ordinary functions (already
        // export-eligible under -shared), so both are emitted + callable.
        var cs = EmitZig(
            "export fn add(a: u8, b: u8) u8 { return a + b; }\n" +
            "pub export fn mul(a: u8, b: u8) u8 { return a * b; }\n" +
            "pub fn main() u8 { return add(mul(20, 2), 2); }\n");
        cs.ShouldContain("add(byte a, byte b)");   // export fn emitted
        cs.ShouldContain("mul(byte a, byte b)");   // pub export fn emitted
        cs.ShouldContain("add(mul(20, 2), 2)");    // both callable locally
    }

    [Fact]
    public void Lowers_export_and_pub_data_globals_like_ordinary_ones()
    {
        // `export const`/`export var`, `pub const`/`pub var`, and `pub export const` are EXPORTED /
        // public DATA; dotcc peels the modifier (Unwrap) and lowers each as an ordinary global (the
        // modifier is a no-op in a console program; a `-shared` data export is a documented cut).
        var cs = EmitZig(
            "export const answer: i32 = 42;\n" +
            "pub const greeting: i32 = 7;\n" +
            "pub var counter: i32 = 0;\n" +
            "export var total: i32 = 0;\n" +
            "pub export const shared: i32 = 3;\n" +
            "pub fn main() u8 { counter = answer; total = greeting + shared; return @intCast(total); }\n");
        cs.ShouldContain("answer = 42");   // export const → an ordinary global field
        cs.ShouldContain("greeting = 7");  // pub const
        cs.ShouldContain("counter");       // pub var (mutable global)
        cs.ShouldContain("total");         // export var
        cs.ShouldContain("shared = 3");    // pub export const
    }

    [Fact]
    public void Lowers_an_extern_c_fn_prototype_like_a_plain_extern_fn()
    {
        // `extern "c" fn` — the optional library/calling-convention string after `extern`. Lowered
        // identically to a plain `extern fn`: the call routes by bare name to dotcc's Libc runtime.
        var cs = EmitZig(
            "extern \"c\" fn putchar(c: c_int) c_int;\n" +
            "pub fn main() u8 { _ = putchar(72); return 0; }\n");
        cs.ShouldContain("putchar(72)");
    }

    [Fact]
    public void Accepts_and_ignores_callconv_align_linksection_modifiers()
    {
        // Milestone R, part 5 — callconv / align / linksection are pure no-ops on the managed target:
        // accepted (so real Zig that uses them round-trips) and lowered exactly like the unmodified
        // decl. `callconv` on the fn, `align` on a local, `linksection` on a global.
        var cs = EmitZig(
            "var counter: u32 linksection(\".mydata\") = 0;\n" +
            "fn tag(x: u8) callconv(.c) u8 { return x + 1; }\n" +
            "pub fn main() u8 {\n" +
            "    var buf: u32 align(8) = 30;\n" +
            "    buf += tag(11);\n" +
            "    counter = buf;\n" +
            "    return @intCast(counter);\n" +
            "}\n");
        cs.ShouldContain("tag(byte x)");      // callconv ignored — an ordinary method
        cs.ShouldContain("x + 1");            // body lowered (the CallConv arg-shift is correct)
        cs.ShouldContain("uint buf = 30");    // align ignored — an ordinary local
        cs.ShouldContain("counter");          // linksection ignored — an ordinary global
    }

    [Fact]
    public void Lowers_a_container_var_and_sibling_const_by_bare_name()
    {
        // Milestone R, part 6 — a container-level `var` is a namespaced mutable global (lowered to a
        // mangled `Cfg_counter` field; `Cfg.counter` reads/writes it), and a sibling const referenced
        // by BARE name in another const's RHS (`doubled = base * 2`) resolves against the container.
        var cs = EmitZig(
            "const Cfg = struct {\n" +
            "    const base: u32 = 10;\n" +
            "    const doubled: u32 = base * 2;\n" +
            "    var counter: u32 = 0;\n" +
            "};\n" +
            "pub fn main() u8 {\n" +
            "    Cfg.counter = Cfg.doubled;\n" +
            "    Cfg.counter += Cfg.base;\n" +
            "    return @intCast(Cfg.counter);\n" +
            "}\n");
        cs.ShouldContain("Cfg_counter = 0");   // the namespaced var → a mangled global field
        cs.ShouldContain("Cfg_counter =");     // `Cfg.counter = …` writes the global
        cs.ShouldContain("Cfg_counter +=");    // `Cfg.counter += …` writes the global
        cs.ShouldContain("10 * 2");            // sibling `base` resolved by bare name inside `doubled`
    }

    [Fact]
    public void Rejects_a_sibling_const_dependency_cycle()
    {
        // `const a = b; const b = a;` — a bare-name sibling cycle errors cleanly (no infinite recurse).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Cfg = struct {\n" +
            "    const a: u32 = b;\n" +
            "    const b: u32 = a;\n" +
            "};\n" +
            "pub fn main() u8 { return @intCast(Cfg.a); }\n"));
        ex.Message.ShouldContain("dependency cycle");
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
    public void Lowers_open_ended_slice_of_a_slice_to_source_len_minus_lo()
    {
        // `s[lo..]` → new ConstSlice<byte>(s.Ptr + lo, s.Len - (ulong)lo); the open high
        // bound is the source `.Len`, NOT a `hi - lo` difference.
        var cs = EmitZig("fn tail(s: []const u8, a: usize) usize { const m = s[a..]; return m.len; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("new ConstSlice<byte>(s.Ptr + a, s.Len - (ulong)a)");
    }

    [Fact]
    public void Lowers_open_ended_slice_of_an_array_to_count_minus_lo()
    {
        // `arr[lo..]` on a `[N]T` array → the open high bound is the element count `N`,
        // so the length is `N - (ulong)lo` and the base decays to its element pointer.
        var cs = EmitZig("pub fn main() u8 { var b: [4]u8 = undefined; b[0] = 1; const s = b[1..]; return s[0]; }\n");
        cs.ShouldContain("new Slice<byte>(b + 1, 4UL - (ulong)1)");
    }

    [Fact]
    public void Rejects_open_ended_slice_of_a_bare_pointer()
    {
        // A `[*c]T` C-pointer has no length, so `p[lo..]` cannot infer a high bound —
        // rejected with a clear error (matching Zig, which also forbids it).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn f(p: [*c]u8) usize { const s = p[1..]; return s.len; }\npub fn main() u8 { return 0; }\n"));
        ex.Message.ShouldContain("open-ended slice");
    }

    [Fact]
    public void Lowers_many_item_pointer_to_a_bare_pointer()
    {
        // `[*]const u8` / `[*]u8` many-item pointers lower to a bare `byte*` (like `[*c]`);
        // indexing `p[i]` and write-through `q[0] = …` work through the pointer.
        var cs = EmitZig(
            "fn at(p: [*]const u8, i: usize) u8 { return p[i]; }\n" +
            "fn w(q: [*]u8) void { q[0] = 1; }\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("at(byte* p, ulong i)");
        cs.ShouldContain("w(byte* q)");
    }

    [Fact]
    public void Closed_slices_a_many_item_pointer_into_a_slice()
    {
        // `p[lo..hi]` on a `[*]const u8` → a length-carrying `ConstSlice<byte>` over the
        // pointer (a many-item pointer slices with explicit bounds, even without `.len`).
        var cs = EmitZig(
            "fn take3(p: [*]const u8) usize { const sl = p[0..3]; return sl.len; }\npub fn main() u8 { return 0; }\n");
        cs.ShouldContain("new ConstSlice<byte>(p + 0");
    }

    [Fact]
    public void Lowers_sentinel_const_pointer_and_slice_like_their_non_sentinel_forms()
    {
        // `[*:0]const u8` (C-string ptr) → bare `byte*` (like `[*]`); `[:0]const u8` →
        // ConstSlice<byte> (like `[]const u8`). A string literal coerces to `[:0]const u8`
        // with `.len` EXCLUDING the NUL (so "hi" → len 2, ctor `(L("hi\0"u8), 2UL)`).
        var cs = EmitZig(
            "fn clen(p: [*:0]const u8) u8 { return p[0]; }\n" +
            "pub fn main() u8 { const s: [:0]const u8 = \"hi\"; return clen(s.ptr); }\n");
        cs.ShouldContain("clen(byte* p)");
        cs.ShouldContain("ConstSlice<byte> s = new ConstSlice<byte>(");
        cs.ShouldContain(", 2UL)");
    }

    [Fact]
    public void Lowers_mutable_sentinel_pointer_and_slice_forms()
    {
        // the non-const sentinel forms `[*:0]u8` / `[:0]u8` also lower — to `byte*` / `Slice<byte>`.
        var cs = EmitZig(
            "fn w(q: [*:0]u8) void { q[0] = 1; }\n" +
            "fn g(s: [:0]u8) usize { return s.len; }\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("w(byte* q)");
        cs.ShouldContain("g(Slice<byte> s)");
    }

    [Fact]
    public void Lowers_sentinel_array_local_to_a_stackalloc_with_a_trailing_sentinel()
    {
        // `[N:0]T` reserves N+1 slots — a literal lays down its N elements plus a trailing `0`
        // sentinel; the symbol's type stays the N-element array so `b[i]` indexes as `[N]T` and
        // `b[N]` reads the sentinel. The sentinel renders as a bare `int` 0 (constant-converts to
        // the element type), NOT an element-typed `0u`.
        var cs = EmitZig(
            "pub fn main() u8 { const b: [3:0]u8 = .{ 1, 2, 3 }; return b[0] + b[3]; }\n");
        cs.ShouldContain("stackalloc byte[]{ 1, 2, 3, 0 }");
    }

    [Fact]
    public void Lowers_undefined_sentinel_array_local_reserving_the_extra_slot()
    {
        // `var b: [3:0]u8 = undefined;` → a zeroed stackalloc of N+1 = 4 (the trailing slot is the
        // sentinel; C# zero-fills the rest), distinct from a plain `[3]u8` (which is `byte[3]`).
        var cs = EmitZig(
            "pub fn main() u8 { var b: [3:0]u8 = undefined; b[0] = 42; return b[0]; }\n");
        cs.ShouldContain("stackalloc byte[4]");
    }

    [Fact]
    public void Lowers_a_non_zero_sentinel_array()
    {
        // Milestone Z lifted the zero-only restriction: a `[N:s]T` array with a non-zero sentinel
        // appends `s` (not `0`) as the trailing slot. `[3:1]u8 = .{1,2,3}` → 4 storage slots ending
        // in the sentinel `1`, the logical type staying the 3-element array.
        var cs = EmitZig(
            "pub fn main() u8 { const b: [3:1]u8 = .{ 1, 2, 3 }; return b[3]; }\n");
        cs.ShouldContain("1, 2, 3, 1"); // the non-zero sentinel `1` appended to the stackalloc literal
    }

    [Fact]
    public void Lowers_a_global_sentinel_array_reserving_the_extra_slot()
    {
        // A `[N:s]T` GLOBAL reserves N+1 slots in the pinned store (like the local stackalloc): a
        // literal appends the sentinel to its element list; the symbol keeps the logical `[N]T` type
        // (so `.len`/slicing exclude the sentinel, and `g[N]` reads it back).
        var cs = EmitZig(
            "const g: [3:9]i32 = .{ 1, 2, 3 };\n" +          // literal → 4 slots ending in 9
            "var z: [2:0]u8 = undefined;\n" +                // undefined zero-sentinel → 3 zeroed slots
            "pub fn main() u8 { z[0] = 5; return @intCast(g[0] + g[3] + z[0]); }\n"); // 1 + 9 + 5 = 15
        cs.ShouldContain(", 9");   // the sentinel appended to the pinned literal store
    }

    // --- Milestone O part 5: non-escaping stack-slice peephole ---------------
    // The synthetic backing-buffer name `__slicebuf` appears ONLY when a slice is promoted to a
    // stackalloc — the embedded runtime always defines `AllocCHeap`/`FreeCHeap` + has incidental
    // `stackalloc byte[N]`, so `__slicebuf` is the reliable promote / no-promote discriminator.

    /// <summary>A page_allocator (devirt'd C-heap) byte slice that is constant-size, freed, and
    /// used only via <c>s[i]</c> is demoted to a <c>stackalloc</c> backing (the heap alloc/free
    /// vanish).</summary>
    [Fact]
    public void Promotes_a_non_escaping_freed_constant_byte_slice_to_a_stackalloc()
    {
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
        cs.ShouldContain("byte* __slicebuf");
        cs.ShouldContain("= stackalloc byte[4]");
        cs.ShouldContain("new Slice<byte>(__slicebuf");
    }

    /// <summary>Returning the slice escapes the frame → it stays on the heap (no promotion).</summary>
    [Fact]
    public void Keeps_a_returned_slice_on_the_heap()
    {
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() ![]u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 4);\n" +
            "    buf[0] = 1;\n" +
            "    return buf;\n" +
            "}\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldNotContain("__slicebuf");
        cs.ShouldContain("AllocCHeap<byte>(4");
    }

    /// <summary>Exposing the backing pointer (<c>s.ptr</c> passed to a callee) escapes → heap.</summary>
    [Fact]
    public void Keeps_a_slice_whose_ptr_escapes_to_a_callee_on_the_heap()
    {
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn use_it(p: [*]u8) void { p[0] = 1; }\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 4);\n" +
            "    use_it(buf.ptr);\n" +
            "    a.free(buf);\n" +
            "    return buf[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldNotContain("__slicebuf");
        cs.ShouldContain("AllocCHeap<byte>(4");
    }

    /// <summary>A non-constant size can't bound the stackalloc → it stays on the heap.</summary>
    [Fact]
    public void Keeps_a_non_constant_size_slice_on_the_heap()
    {
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run(n: usize) !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, n);\n" +
            "    buf[0] = 1;\n" +
            "    a.free(buf);\n" +
            "    return buf[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run(4) catch 1; }\n");
        cs.ShouldNotContain("__slicebuf");
    }

    /// <summary>An un-freed slice stays on the heap (no <c>free</c> to drop → not promotable).</summary>
    [Fact]
    public void Keeps_an_unfreed_slice_on_the_heap()
    {
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, 4);\n" +
            "    buf[0] = 1;\n" +
            "    return buf[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldNotContain("__slicebuf");
        cs.ShouldContain("AllocCHeap<byte>(4");
    }

    /// <summary>A FixedBufferAllocator slice — even FBA-site-DEVIRTUALIZED (Milestone U,
    /// <c>FbaCtx != null</c>) — is left on its allocator: the stack-slice peephole promotes only the
    /// devirtualized C-heap default (<c>Receiver == null &amp;&amp; FbaCtx == null</c>).</summary>
    [Fact]
    public void Keeps_a_devirtualized_fba_slice_unpromoted()
    {
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    const aa = fba.allocator();\n" +
            "    const buf = try aa.alloc(u8, 4);\n" +
            "    buf[0] = 5;\n" +
            "    aa.free(buf);\n" +
            "    return buf[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldNotContain("__slicebuf");                 // NOT promoted (FBA-devirt stays on its allocator)
        cs.ShouldContain("ZigAlloc.AllocFba<byte>(&fba, 4");   // FBA-site devirt (no vtable)
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
        // A NON-CONSTANT size (the `n` param) keeps this devirt'd-but-heap: the Milestone O part 5
        // stack-slice peephole would otherwise demote a constant-size, non-escaping, freed slice to
        // a `stackalloc` (eliding the very AllocCHeap/FreeCHeap calls this test pins).
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run(n: usize) !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const buf = try a.alloc(u8, n);\n" +
            "    buf[0] = 42;\n" +
            "    const r = buf[0];\n" +
            "    a.free(buf);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return run(4) catch 1; }\n");
        cs.ShouldContain("ZigAlloc.AllocCHeap<byte>(");    // the devirt'd alloc (direct malloc)
        cs.ShouldContain("ZigAlloc.FreeCHeap<byte>(buf)");  // the devirt'd free (direct free)
        cs.ShouldNotContain(".Alloc<byte>(");               // NOT the indirect vtable dispatch
        cs.ShouldNotContain("Allocator a =");               // the comptime binding emits no decl
    }

    [Fact]
    public void Devirtualizes_a_fixed_buffer_allocator_site()
    {
        // `const a = fba.allocator();` over a known FixedBufferAllocator local — DEVIRTUALIZED
        // (Milestone U): `a` carries no runtime decl, and `a.alloc(…)` becomes a direct
        // `ZigAlloc.AllocFba<T>(&fba, …)` (no vtable load). The `&fba` is the real local (an lvalue),
        // not a per-call copy, so the bump cursor is shared.
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
        cs.ShouldContain("ZigAlloc.AllocFba<byte>(&fba, 3");      // DEVIRTUALIZED FBA bump (no vtable)
        cs.ShouldNotContain("Allocator a =");                     // the FBA-site binding emits no decl
        cs.ShouldNotContain(".Alloc<byte>(");                     // NOT the indirect vtable dispatch
    }

    [Fact]
    public void Lowers_a_passed_fba_allocator_through_the_indirect_vtable()
    {
        // When `fba.allocator()` is PASSED to an opaque `std.mem.Allocator` parameter (not bound to a
        // provable const), it materializes `ZigAlloc.FbaAllocator(&fba)` at the call and the callee
        // dispatches INDIRECTLY through the vtable — the FBA-site devirt applies only to a bound const.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn fill(a: std.mem.Allocator) !u8 {\n" +
            "    const s = try a.alloc(u8, 3);\n" +
            "    s[0] = 42;\n" +
            "    return s[0];\n" +
            "}\n" +
            "fn run() !u8 {\n" +
            "    var buffer: [64]u8 = undefined;\n" +
            "    var fba = std.heap.FixedBufferAllocator.init(&buffer);\n" +
            "    return fill(fba.allocator());\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("ZigAlloc.FbaAllocator(&fba)");   // the allocator() fat pointer, materialized at the call
        cs.ShouldContain("a.Alloc<byte>(3");                // INDIRECT vtable dispatch inside the callee
        cs.ShouldNotContain("ZigAlloc.AllocFba<");          // NOT devirt'd (the allocator is opaque to `fill`)
    }

    [Fact]
    public void Devirtualizes_realloc_to_a_direct_c_heap_call()
    {
        // `a.realloc(s, n)` (Milestone U) on the statically-known default → a direct
        // `ZigAlloc.ReallocCHeap` (Libc.realloc, no vtable). The initial constant-size alloc is NOT
        // promoted to stackalloc (a realloc'd slice escapes the peephole), so AllocCHeap survives.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    var s = try a.alloc(u8, 2);\n" +
            "    s[0] = 30;\n" +
            "    s = try a.realloc(s, 4);\n" +
            "    s[2] = 12;\n" +
            "    const r = s[0] + s[2];\n" +
            "    a.free(s);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("ZigAlloc.AllocCHeap<byte>(2");     // initial alloc survives (not promoted)
        cs.ShouldContain("ZigAlloc.ReallocCHeap<byte>(");    // the devirt'd realloc (direct Libc.realloc)
        cs.ShouldNotContain(".Realloc<byte>(");              // NOT the indirect vtable emulation
        cs.ShouldNotContain("__slicebuf");                   // NOT stackalloc-promoted
    }

    [Fact]
    public void Lowers_realloc_through_the_indirect_vtable()
    {
        // On an opaque `std.mem.Allocator` parameter, realloc dispatches INDIRECTLY — `a.Realloc<T>`
        // — which the runtime EMULATES via the 2-fn vtable (alloc + copy + free).
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn grow(a: std.mem.Allocator) ![]u8 {\n" +
            "    var s = try a.alloc(u8, 2);\n" +
            "    s = try a.realloc(s, 4);\n" +
            "    return s;\n" +
            "}\n" +
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("a.Realloc<byte>(");                // INDIRECT (emulated) realloc on the param
        cs.ShouldNotContain("ZigAlloc.ReallocCHeap<");       // NOT devirt'd (opaque allocator)
    }

    [Fact]
    public void Rejects_deferred_resize_and_remap()
    {
        // `resize` (bool, in-place) / `remap` (?[]T) are deferred — their result is allocator-page-
        // dependent — so they're a clear error, not a divergent guess. Use `realloc`.
        foreach (var method in new[] { "resize", "remap" })
        {
            var ex = Should.Throw<CompileException>(() => EmitZig(
                "const std = @import(\"std\");\n" +
                "fn run() !u8 {\n" +
                "    const a = std.heap.page_allocator;\n" +
                "    const s = try a.alloc(u8, 2);\n" +
                "    _ = a." + method + "(s, 4);\n" +
                "    a.free(s);\n" +
                "    return s[0];\n" +
                "}\n" +
                "pub fn main() u8 { return run() catch 1; }\n"));
            ex.Message.ShouldContain("deferred");
        }
    }

    [Fact]
    public void Lowers_an_arena_allocator_with_deinit()
    {
        // `std.heap.ArenaAllocator.init(backing)` → ArenaAllocator.Init; `arena.allocator()` →
        // ZigAlloc.ArenaToAllocator(&arena) (an opaque Allocator → INDIRECT .Alloc); `arena.deinit()`
        // → ZigAlloc.ArenaDeinit(&arena), wrapped by `defer` into a try/finally (Milestone U).
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);\n" +
            "    defer arena.deinit();\n" +
            "    const a = arena.allocator();\n" +
            "    const s = try a.alloc(u8, 3);\n" +
            "    s[0] = 42;\n" +
            "    return s[0];\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("ArenaAllocator.Init(ZigAlloc.CHeap())");   // arena over the materialized default
        cs.ShouldContain("ArenaAllocator arena =");                   // a runtime ArenaAllocator local
        cs.ShouldContain("ZigAlloc.ArenaToAllocator(&arena)");        // allocator() fat pointer
        cs.ShouldContain(".Alloc<byte>(3");                           // INDIRECT vtable dispatch
        cs.ShouldContain("ZigAlloc.ArenaDeinit(&arena)");             // deinit
        cs.ShouldContain("finally");                                   // defer → try/finally
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
    public void Devirtualizes_create_and_destroy_to_direct_c_heap_calls()
    {
        // `a.create(T)` / `a.destroy(p)` (single-object alloc, Milestone U) on the statically-known
        // default DEVIRTUALIZE to a direct ZigAlloc.CreateCHeap / DestroyCHeap (a Libc.malloc/free,
        // no vtable). `create`'s `Error!*T` rides as `ErrUnion<nuint>` (a pointer can't be an
        // `ErrUnion<T>` generic arg); `try` casts the unwrapped address back to `T*`.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn run() !u8 {\n" +
            "    const a = std.heap.page_allocator;\n" +
            "    const p = try a.create(u8);\n" +
            "    p.* = 42;\n" +
            "    const r = p.*;\n" +
            "    a.destroy(p);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return run() catch 1; }\n");
        cs.ShouldContain("ZigAlloc.CreateCHeap<byte>(");                       // the devirt'd create
        cs.ShouldContain("(byte*)ErrUnion.Try(ZigAlloc.CreateCHeap<byte>(");   // the nuint -> T* cast on try
        cs.ShouldContain("ZigAlloc.DestroyCHeap<byte>(p)");                     // the devirt'd destroy
        cs.ShouldNotContain(".Create<byte>(");                                  // NOT the indirect vtable dispatch
    }

    [Fact]
    public void Lowers_create_and_destroy_through_the_indirect_vtable()
    {
        // On an opaque `std.mem.Allocator` parameter, create/destroy dispatch INDIRECTLY through the
        // vtable — `a.Create<T>(oom)` / `a.Destroy<T>(p)` — exactly like alloc/free.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "fn use_it(a: std.mem.Allocator) !u8 {\n" +
            "    const p = try a.create(u8);\n" +
            "    p.* = 7;\n" +
            "    const r = p.*;\n" +
            "    a.destroy(p);\n" +
            "    return r;\n" +
            "}\n" +
            "pub fn main() u8 { return use_it(std.heap.page_allocator) catch 1; }\n");
        cs.ShouldContain("a.Create<byte>(");                       // INDIRECT vtable dispatch
        cs.ShouldContain("(byte*)ErrUnion.Try(a.Create<byte>(");   // the nuint -> T* cast on try
        cs.ShouldContain("a.Destroy<byte>(p)");                    // indirect destroy
        cs.ShouldNotContain("ZigAlloc.CreateCHeap<byte>(");        // NOT devirt'd at the call site
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
    public void Destructures_an_inline_tuple_literal_element_wise()
    {
        // `const a, const b = .{ … };` — a tuple-LITERAL RHS lowers ELEMENT-WISE in source order
        // (Milestone S): each binder takes its own element directly, with NO snapshot temp / tuple
        // value. (A non-literal tuple RHS still single-evals into `__tup`; see the test above.)
        var cs = EmitZig(
            "pub fn main() u8 { const a, const b = .{ @as(u8, 4), @as(u8, 6) }; return a + b; }\n");
        cs.ShouldContain("byte a = (byte)4;");
        cs.ShouldContain("byte b = (byte)6;");
        cs.ShouldNotContain("__tup");          // no snapshot temp for a literal RHS
        cs.ShouldNotContain("ValueTuple");     // no tuple value constructed at all
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
    public void Lowers_an_empty_tuple_and_an_over_seven_arity_tuple()
    {
        // An empty `.{}` (no struct sink) → the non-generic `System.ValueTuple`; an arity > 7 tuple
        // nests via the 8th `TRest` field (`ValueTuple<T1..T7, ValueTuple<T8..>>`), and an index ≥ 7
        // reads through `.Rest`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const e = .{};\n" +
            "    _ = e;\n" +
            "    const t = .{ @as(u8, 1), @as(u8, 2), @as(u8, 3), @as(u8, 4), @as(u8, 5), @as(u8, 6), @as(u8, 7), @as(u8, 8), @as(u8, 9) };\n" +
            "    return t[0] + t[7] + t[8];\n" + // 1 + 8 + 9 = 18
            "}\n");
        cs.ShouldContain("default(System.ValueTuple)");   // empty tuple `.{}`
        cs.ShouldContain("System.ValueTuple<byte, byte>>"); // the nested TRest (>7 arity)
        cs.ShouldContain(".Rest.Item1");                   // t[7] reads through the nested tuple
        cs.ShouldContain(".Rest.Item2");                   // t[8]
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
    public void Destructures_to_existing_lvalues_element_wise()
    {
        // Milestone S — `a, b = .{ … };` assigns to EXISTING lvalues. A tuple-literal RHS lowers
        // element-wise in source order with no temp, faithful to Zig's sequential semantics: a later
        // element's read sees an earlier element's write (so `a, b = .{ b, a }` is NOT a swap).
        var cs = EmitZig(
            "pub fn main() u8 { var a: u8 = 3; var b: u8 = 9; a, b = .{ b, a }; _ = &a; _ = &b; return a + b - 12; }\n");
        cs.ShouldContain("a = b;");
        cs.ShouldContain("b = a;");
        cs.ShouldNotContain("__tup");   // element-wise, no snapshot
    }

    [Fact]
    public void Destructures_with_typed_binders()
    {
        // Milestone S — `const e: u16, const f: u8 = .{ 300, 7 };`. Each binder's declared type is
        // its element's result location, so a bare `300` lands as a `ushort` (u16), `7` as a `byte`.
        var cs = EmitZig(
            "pub fn main() u8 { const e: u16, const f: u8 = .{ 300, 7 }; return @as(u8, @intCast(e - 293)) + f; }\n");
        cs.ShouldContain("ushort e = 300;");
        cs.ShouldContain("byte f = 7;");
    }

    [Fact]
    public void Destructures_mixed_new_and_existing_binders()
    {
        // Milestone S — a fresh `const d` interleaved with an existing lvalue `c`: the new binder
        // declares, the existing one assigns, in source order.
        var cs = EmitZig(
            "pub fn main() u8 { var c: u8 = 0; const d, c = .{ 5, 6 }; _ = &c; return d + c - 11; }\n");
        cs.ShouldContain("int d = 5;");   // untyped `const d` infers int from the literal element
        cs.ShouldContain("c = 6;");       // existing lvalue assigned
    }

    [Fact]
    public void Destructures_with_a_discard_binder()
    {
        // Milestone S — a `_` binder ignores its element. A side-effect-free element (the literal 99)
        // simply isn't bound; the sibling `g` still takes its element.
        var cs = EmitZig(
            "pub fn main() u8 { var g: u8 = 0; _, g = .{ 99, 8 }; _ = &g; return g; }\n");
        cs.ShouldContain("g = 8;");
        cs.ShouldNotContain("__tup");
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
    public void Lowers_exponent_only_float_literals()
    {
        // A decimal float with an exponent but NO fraction dot (`1e3`, `4E2`) — Zig allows it, but the
        // old FLOAT lexer rule required a `.`, so `1e3` mis-lexed as `INTEGER 1` + `IDENT e3` and the
        // parse failed. It now lexes as one FLOAT and passes through as a C# double literal verbatim.
        var cs = EmitZig(
            "const A: f64 = 1e3;\n" +   // 1000.0 — lowercase e
            "const B: f64 = 4E2;\n" +   // 400.0  — uppercase E
            "pub fn main() u8 { return 0; }\n");
        cs.ShouldContain("A = 1e3");
        cs.ShouldContain("B = 4E2");
    }

    [Fact]
    public void Lowers_a_non_bmp_char_literal_to_its_codepoint_int()
    {
        // A Zig char literal is a `comptime_int` equal to the codepoint, so a non-BMP `\u{…}` needs no
        // surrogate handling — it lowers to the plain integer codepoint (U+1F600 😀 = 128512), exactly
        // as a BMP one does (U+263A ☺ = 9786). (A small-int SINK that can't hold it is a type-fit error,
        // which real Zig rejects too; the literal itself is faithful.)
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 { _ = printf(\"%d %d\", @as(c_int, '\\u{1F600}'), @as(c_int, '\\u{263A}')); return 0; }\n");
        cs.ShouldContain("128512");   // U+1F600 codepoint (non-BMP)
        cs.ShouldContain("9786");     // U+263A codepoint (BMP)
    }

    [Fact]
    public void Lowers_std_mem_eql_and_copyForwards()
    {
        // `std.mem.eql(T, a, b)` → `ZigMem.Eql<T>`; `std.mem.copyForwards(T, dest, src)` →
        // `ZigMem.CopyForwards<T>`. Both take the element type as arg0; the slice args coerce a
        // `&array` (`*[N]T`) to a `[]T` / `[]const T` fat pointer.
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2, 3 };\n" +
            "    const b = [_]u8{ 1, 2, 3 };\n" +
            "    var d = [_]u8{ 0, 0, 0 };\n" +
            "    const eq: u8 = if (std.mem.eql(u8, &a, &b)) 1 else 0;\n" +
            "    std.mem.copyForwards(u8, &d, &a);\n" +
            "    return eq + d[0];\n" +
            "}\n");
        cs.ShouldContain("ZigMem.Eql<byte>(");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(");
        cs.ShouldContain("new ConstSlice<byte>(a,");   // &array → []const u8 coercion
    }

    [Fact]
    public void Lowers_memcpy_and_memset_builtins_as_bare_statements()
    {
        // `@memset(dest, v)` → `ZigMem.Set<T>`; `@memcpy(dest, src)` → `ZigMem.CopyForwards<T>`.
        // Element type inferred from the dest. Both are void, so they render as BARE statements —
        // NOT `_ = ZigMem.…` (which would be a `_ = <void>` C# error).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var buf = [_]u8{ 9, 9, 9, 9 };\n" +
            "    @memset(&buf, 7);\n" +
            "    var b2 = [_]u8{ 0, 0, 0 };\n" +
            "    const a = [_]u8{ 1, 2, 3 };\n" +
            "    @memcpy(&b2, &a);\n" +
            "    return buf[0] + b2[0];\n" +
            "}\n");
        cs.ShouldContain("ZigMem.Set<byte>(");
        cs.ShouldContain("ZigMem.CopyForwards<byte>(");
        cs.ShouldNotContain("_ = ZigMem.");   // void mem calls are bare statements
    }

    [Fact]
    public void Coerces_addr_of_array_to_a_slice_parameter()
    {
        // Zig's `*[N]T` → `[]T`: passing `&arr` where a `[]const T` slice is expected (a pre-existing
        // coercion gap the std.mem work closed — `CoerceToSlice` now strips the address-of).
        var cs = EmitZig(
            "fn n(s: []const u8) usize { return s.len; }\n" +
            "pub fn main() u8 {\n" +
            "    const a = [_]u8{ 1, 2, 3 };\n" +
            "    return @intCast(n(&a));\n" +
            "}\n");
        cs.ShouldContain("new ConstSlice<byte>(a,");
    }

    [Fact]
    public void Lowers_std_mem_span_and_zeroes()
    {
        // `std.mem.span(ptr)` → `ZigMem.SpanZ<T>` (NUL-sentinel scan → `[]const T`); `std.mem.zeroes(T)`
        // → C#'s `default(T)` (zero scalar / struct).
        var cs = EmitZig(
            "const std = @import(\"std\");\n" +
            "const Point = struct { x: i32, y: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    const p: [*:0]const u8 = \"hi\";\n" +
            "    const s = std.mem.span(p);\n" +
            "    const zi: i32 = std.mem.zeroes(i32);\n" +
            "    const zp = std.mem.zeroes(Point);\n" +
            "    return @intCast(s.len + @as(usize, @intCast(zi + zp.x)));\n" +
            "}\n");
        cs.ShouldContain("ZigMem.SpanZ<byte>(");
        cs.ShouldContain("default(");          // zeroes → default(T)
    }

    [Fact]
    public void Rejects_std_mem_zeroes_of_an_array_type()
    {
        // An array `zeroes` would need a zeroed array VALUE; arrays lower to a pointer, so `default`
        // is a null pointer — a clear cut, not a silent wrong value.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 { const z = std.mem.zeroes([4]u8); return z[0]; }\n"));
        ex.Message.ShouldContain("zeroes");
        ex.Message.ShouldContain("array");
    }

    [Fact]
    public void Rejects_an_unmodeled_std_mem_member()
    {
        // dotcc models a curated std.mem subset; anything else is a clear, specific error (not a
        // silent miscompile) — "fail loudly, grow deliberately".
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const std = @import(\"std\");\n" +
            "pub fn main() u8 { var a = [_]u8{ 1, 2 }; std.mem.reverse(u8, &a); return 0; }\n"));
        ex.Message.ShouldContain("std.mem.reverse");
        ex.Message.ShouldContain("not modeled");
    }

    [Fact]
    public void Lowers_escaped_quote_and_unicode_escape_in_a_string()
    {
        // `\"` is an escaped quote (the old `"[^"]*"` rule truncated there); `\u{NNNN}` expands to
        // its UTF-8 bytes — U+2764 (❤) = E2 9D A4, which (being > 0x7F) routes to the byte-array path.
        // A non-BMP codepoint (U+1F600 😀 = F0 9F 98 80, 4 bytes) also works: `char.ConvertFromUtf32`
        // yields the surrogate-pair string that UTF-8 then encodes as four bytes — no special handling.
        var cs = EmitZig(
            "extern fn printf(format: [*c]const u8, ...) c_int;\n" +
            "pub fn main() u8 { _ = printf(\"a\\\"b\"); _ = printf(\"\\u{2764}\\u{1F600}\"); return 0; }\n");
        cs.ShouldContain("a\\\"b");                            // the escaped quote survived into the u8 literal
        cs.ShouldContain("0xE2, 0x9D, 0xA4");                  // U+2764 UTF-8 bytes (BMP)
        cs.ShouldContain("0xF0, 0x9F, 0x98, 0x80");            // U+1F600 UTF-8 bytes (non-BMP, surrogate-safe)
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
    public void Lowers_a_labeled_value_block_in_an_if_expression_arm_at_a_typed_init()
    {
        // Milestone Y, part 1 LIFTED the earlier cut: a labeled value-block as an `if`-expression arm
        // at a full (here typed `i32`) `const` init no longer rejects. The ternary an if-expression
        // would lower to can't host the block's statements, so dotcc lowers the `if` as a STATEMENT
        // filling a result temp (`__vcf`), the typed annotation flowing in as the temp's sink.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const x: i32 = if (true) blk: { break :blk 1; } else 0;\n" +
            "    return @as(u8, @intCast(x));\n" +
            "}\n");
        cs.ShouldContain("__vcf");      // the value-control-flow result temp
        cs.ShouldContain("goto __blk"); // the labeled value-block branch lowered to temp + goto
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
    public void Rejects_a_labeled_value_break_targeting_a_statement_loop()
    {
        // `break :lbl <value>` yields from a labeled VALUE loop (Milestone Y part 2 supports that —
        // `lbl: while/for (…) {…} else d`). But here `lp` names a labeled STATEMENT loop with no
        // `else` (a value break needs a value loop), so it's still a clear error — guiding toward an
        // `else`. (This is also invalid Zig: a labeled `break v` requires the loop be an expression.)
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 {\n" +
            "    lp: while (true) { break :lp 5; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("statement loop");
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
    public void Hoists_a_side_effecting_catch_in_a_sub_expression()
    {
        // ANF (sub-expression positions): a side-effecting `catch` in a SUB-expression (not a full
        // RHS) is hoisted to a `__anfN` temp before the enclosing statement — the lazy ternary runs
        // in the buffer, and the containing expression reads the temp.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "fn dflt() i32 { return 13; }\n" +
            "pub fn main() u8 {\n" +
            "    const v: i32 = 1 + (mk(false) catch dflt());\n" + // catch is a sub-operand of `+`
            "    return @as(u8, @intCast(v));\n" +
            "}\n");
        cs.ShouldContain("__cE.IsErr");             // the lazy catch machinery ran (hoisted)
        cs.ShouldContain("__anf");                  // a result temp was hoisted before the decl
        cs.ShouldContain("1 + __anf");              // the containing `+` reads the hoisted temp
    }

    [Fact]
    public void Rejects_a_hoisted_catch_past_a_side_effecting_operand()
    {
        // Eval-order correctness: a `catch` can't be hoisted before the statement if a side effect was
        // already evaluated earlier in the same statement (`f() + (a catch b)` must run `f()` first).
        // A clear error (not a silent reorder) — the user binds it to a `const` first.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 5; return error.Bad; }\n" +
            "fn f() i32 { return 1; }\n" +
            "fn dflt() i32 { return 2; }\n" +
            "pub fn main() u8 { return @as(u8, @intCast(f() + (mk(false) catch dflt()))); }\n"));
        ex.Message.ShouldContain("side-effecting operand");
    }

    [Fact]
    public void Lowers_an_error_set_declaration()
    {
        // Milestone N, part 5: `const E = error{A, B};` is a comptime declaration — dotcc erases the
        // set, so it registers the member names and emits NO `E` decl; `E` is used only as the
        // (ignored) set in an `E!T` return type, which lowers to the same `ErrUnion<T>` as
        // `anyerror!T`. `error.X` members + `catch` work as in the flat-code model.
        var cs = EmitZig(
            "const MathError = error{ Overflow, Negative };\n" +
            "fn scale(n: i32) MathError!i32 { if (n < 0) return error.Negative; return n; }\n" +
            "pub fn main() u8 { const v = scale(5) catch 0; return @as(u8, @intCast(v)); }\n");
        cs.ShouldContain("ErrUnion<int> scale(");     // `MathError!i32` → the erased ErrUnion
        cs.ShouldNotContain("MathError");             // the error-set decl emits nothing (comptime)
    }

    [Fact]
    public void Rejects_an_error_set_literal_in_value_position()
    {
        // `error{…}` is only a `const E = error{…};` declaration or an (ignored) `E!T` set — a bare
        // error-set literal used as a value is a clear error.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "pub fn main() u8 { const v = 1 + error{Bad}; return @as(u8, @intCast(v)); }\n"));
        ex.Message.ShouldContain("error{");
    }

    [Fact]
    public void Lowers_a_catch_return()
    {
        // Milestone N, part 6: `const v = a catch return [x];` — hoist the union, `if (IsErr) return …;`
        // (early-out, the `return error.X` wraps as an Err in the `!T` fn), then bind the payload.
        var cs = EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 10; return error.Bad; }\n" +
            "fn wrap(ok: bool) !i32 { const v = mk(ok) catch return error.Wrapped; return v + 1; }\n" +
            "pub fn main() u8 { return @as(u8, @intCast(wrap(true) catch 0)); }\n");
        cs.ShouldContain("ErrUnion<int> __cf = mk");   // the union is hoisted
        cs.ShouldContain("Cond.B(__cf.IsErr)");         // error → early return
        cs.ShouldContain("ErrUnion<int>.Err(");         // `return error.Wrapped` wraps as Err
        cs.ShouldContain("int v = __cf.Value;");        // success → bind the payload
    }

    [Fact]
    public void Lowers_an_orelse_return()
    {
        // `const v = a orelse return [x];` — on the optional's none, early-return; else bind the payload.
        var cs = EmitZig(
            "fn pick(b: bool) ?i32 { if (b) return 20; return null; }\n" +
            "fn fetch(b: bool) i32 { const v = pick(b) orelse return 7; return v; }\n" +
            "pub fn main() u8 { return @as(u8, @intCast(fetch(true))); }\n");
        cs.ShouldContain("int? __cf = pick");   // the optional is hoisted
        cs.ShouldContain("__cf.HasValue");       // none → early return
        cs.ShouldContain("return 7;");           // the orelse fallback
        cs.ShouldContain("int v = __cf.Value;"); // success → bind the payload
    }

    [Fact]
    public void Lowers_a_statement_catch_return()
    {
        // `a catch return …;` as a STATEMENT (the `!void` value discarded) — common for an early-out.
        var cs = EmitZig(
            "fn step(go: bool) !void { if (go) return error.Bad; }\n" +
            "fn run(go: bool) !u8 { step(go) catch return error.Stop; return 7; }\n" +
            "pub fn main() u8 { return run(false) catch 9; }\n");
        cs.ShouldContain("ErrUnion<Unit> __cf = step");   // `!void` → ErrUnion<Unit>
        cs.ShouldContain("Cond.B(__cf.IsErr)");            // error → early return
        cs.ShouldContain("ErrUnion<byte>.Err(");           // `return error.Stop` in the `!u8` fn
    }

    [Fact]
    public void Hoists_a_control_flow_fallback_in_a_sub_expression()
    {
        // ANF: `a orelse return x` / `a catch return x` in a SUB-expression hoists — the conditional
        // `return` and the payload capture become buffer statements before the enclosing statement,
        // and the construct evaluates to the payload temp.
        var cs = EmitZig(
            "fn pick(b: bool) ?i32 { if (b) return 20; return null; }\n" +
            "fn g(b: bool) u8 { const v: i32 = 10 + (pick(b) orelse return 7); return @as(u8, @intCast(v)); }\n" +
            "pub fn main() u8 { return g(true); }\n");
        cs.ShouldContain("__cf.HasValue");   // the control-flow fallback lowered (hoisted)
        cs.ShouldContain("return 7;");       // the early-return on the none path
        cs.ShouldContain("10 + __anf");      // the containing `+` reads the hoisted payload temp
    }

    [Fact]
    public void Hoists_a_parenthesized_value_if_in_a_sub_expression()
    {
        // ANF Phase B: a parenthesized value-position control-flow construct — here a BLOCK-BODIED
        // `if` — in a sub-expression. `( RhsExpr )` makes it a Primary; a block-bodied branch lowers
        // to a statement filling a `__vcf` temp, which is hoisted before the enclosing statement.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const c = true;\n" +
            "    const r: i32 = 2 + (if (c) blk: { break :blk @as(i32, 40); } else @as(i32, 0));\n" +
            "    return @as(u8, @intCast(r));\n" +
            "}\n");
        cs.ShouldContain("__vcf");        // the value-if lowered to a result-temp fill (hoisted)
        cs.ShouldContain("2 + __vcf");    // the containing `+` reads the hoisted temp
    }

    [Fact]
    public void Keeps_a_parenthesized_simple_value_if_as_an_inline_ternary()
    {
        // A SIMPLE (bare-expr) value-if in a paren needs no hoist — it stays a C# ternary inline. (The
        // `( RhsExpr )` grammar is what lets it PARSE in a sub-expression; only block-bodied branches
        // hoist.)
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const c = true;\n" +
            "    const r: i32 = 1 + (if (c) @as(i32, 20) else @as(i32, 8));\n" +
            "    return @as(u8, @intCast(r));\n" +
            "}\n");
        cs.ShouldContain("? (int)20 : (int)8");   // inline ternary, no hoist
        cs.ShouldNotContain("__vcf");
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
    public void Lowers_a_capture_while_with_an_else_clause()
    {
        // `while (opt) |x| body else elsebody` — the else runs on natural exit (payload null). The
        // exit branch of the desugared `if` becomes `{ elsebody; break; }` (not a bare `break`).
        var cs = EmitZig(
            "fn nextLT(i: *i32, max: i32) ?i32 { if (i.* >= max) return null; const v = i.*; i.* += 1; return v; }\n" +
            "pub fn main() u8 {\n" +
            "    var i: i32 = 0;\n" +
            "    var sum: i32 = 0;\n" +
            "    while (nextLT(&i, 3)) |v| { sum += v; } else { sum += 100; }\n" +
            "    return @as(u8, @intCast(sum));\n" +
            "}\n");
        cs.ShouldContain("while (Cond.B(true))");
        cs.ShouldContain("int v = __cap.Value;");
        cs.ShouldContain("sum += 100");   // the else body runs on natural exit
        cs.ShouldContain("break;");
    }

    [Fact]
    public void Lowers_a_capture_while_with_a_continue_expression()
    {
        // `while (opt) |x| : (cont) body` → the C `For` IR (post = cont), so `continue` runs the cont.
        var cs = EmitZig(
            "fn nextLT(i: *i32, max: i32) ?i32 { if (i.* >= max) return null; const v = i.*; i.* += 1; return v; }\n" +
            "pub fn main() u8 {\n" +
            "    var i: i32 = 0;\n" +
            "    var cnt: i32 = 0;\n" +
            "    var sum: i32 = 0;\n" +
            "    while (nextLT(&i, 4)) |v| : (cnt = cnt + 1) { sum += v; }\n" +
            "    return @as(u8, @intCast(sum + cnt));\n" +
            "}\n");
        cs.ShouldContain("for (");            // the cont form lowers to a `for` (post = cont)
        cs.ShouldContain("cnt = cnt + 1");    // the continue-expression as the for post
        cs.ShouldContain("int v = __cap.Value;");
    }

    [Fact]
    public void Lowers_an_error_union_capture_while_with_else_capture()
    {
        // `while (eu) |x| body else |e| elsebody` — bind the success payload each turn; on error bind
        // `e` (the flat code) and run the else-branch, then break. Mirrors the if-capture error arm.
        var cs = EmitZig(
            "const E = error{ Stop };\n" +
            "fn step(i: *i32) E!i32 { if (i.* < 3) { i.* += 1; return i.*; } return error.Stop; }\n" +
            "pub fn main() u8 {\n" +
            "    var i: i32 = 0;\n" +
            "    var sum: i32 = 0;\n" +
            "    var code: i32 = 0;\n" +
            "    while (step(&i)) |v| { sum += v; } else |e| { code = if (e == error.Stop) 9 else 1; }\n" +
            "    return @as(u8, @intCast(sum + code));\n" +
            "}\n");
        cs.ShouldContain("Cond.B(__cap.IsErr)");   // error branch = the `if` test
        cs.ShouldContain("__cap.Value");           // success binding
        cs.ShouldContain("__cap.Code");            // the captured error code
        cs.ShouldContain("break;");
    }

    [Fact]
    public void Lowers_a_for_slice_with_a_non_zero_index_start()
    {
        // `for (s, N..) |x, i|` — the index capture starts at N (not 0): `var i = __i + N;`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const arr = [_]u8{ 10, 20, 30 };\n" +
            "    var acc: i32 = 0;\n" +
            "    for (arr[0..], 5..) |x, idx| { acc += @as(i32, x) + @as(i32, @intCast(idx)); }\n" +
            "    return @as(u8, @intCast(acc));\n" +
            "}\n");
        cs.ShouldContain("__i + (ulong)5");   // index starts at 5, not 0
    }

    [Fact]
    public void Rejects_an_error_union_capture_while_without_else_capture()
    {
        // An error-union capture-while must handle the error — a bare `while (eu) |x| {}` with no
        // `else |e|` is a clear error (not a silent drop of the error path).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn mk(ok: bool) !i32 { if (ok) return 7; return error.Bad; }\n" +
            "pub fn main() u8 {\n" +
            "    while (mk(true)) |x| { _ = x; }\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("else |e|");
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

    // ---- Milestone P, part 1: wrapping arithmetic (`+%` / `-%` / `*%` + compound) ----

    [Fact]
    public void Lowers_wrapping_add_at_a_sub_int_width_with_a_truncating_cast()
    {
        // `a +% b` on `u8` operands wraps at 8 bits. C# would promote the operands to `int`, so a
        // truncating `(byte)` cast back to the operand width is inserted — exactly two's-complement
        // wrap in the project's unchecked context (200 + 100 = 300 → 44).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 200;\n" +
            "    const b: u8 = 100;\n" +
            "    const c: u8 = a +% b;\n" +
            "    return c;\n" +
            "}\n");
        cs.ShouldContain("(byte)(a + b)");
    }

    [Fact]
    public void Lowers_wrapping_mul_with_a_truncating_cast()
    {
        // `*%` is multiplicative-precedence; on `u8` it wraps at 8 bits (16 * 16 = 256 → 0).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 16;\n" +
            "    const b: u8 = 16;\n" +
            "    const c: u8 = a *% b;\n" +
            "    return c;\n" +
            "}\n");
        cs.ShouldContain("(byte)(a * b)");
    }

    [Fact]
    public void Wraps_at_the_operand_width_before_widening()
    {
        // The wrap is at the OPERAND width, not the result location: `u8 +% u8` wraps at 8 bits
        // (300 → 44) and only then widens to the `u32` sink — so the truncating cast is `(byte)`,
        // never `(uint)`. (A naive "wrap at the sink width" would give 300, the bug this guards.)
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 200;\n" +
            "    const b: u8 = 100;\n" +
            "    const w: u32 = a +% b;\n" +
            "    return @as(u8, @intCast(w - 2));\n" +
            "}\n");
        cs.ShouldContain("uint w = (byte)(a + b)");
    }

    [Fact]
    public void Lowers_wrapping_at_int_width_without_a_cast()
    {
        // At `int` and wider, native C# arithmetic already wraps at the operand width in the
        // unchecked context, so no extra truncating cast is emitted.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: i32 = 100;\n" +
            "    const b: i32 = 200;\n" +
            "    const c: i32 = a +% b;\n" +
            "    return @as(u8, @intCast(c - 258));\n" +
            "}\n");
        cs.ShouldContain("a + b");
        cs.ShouldNotContain("(int)(a + b)");
    }

    [Fact]
    public void Lowers_a_wrapping_compound_assignment_to_a_native_compound_op()
    {
        // `x +%= y` is a native C# `x += y` — the compound operator already truncates back to the
        // LHS width (unchecked), so it is observably identical to the plain `+=` form.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var x: u8 = 40;\n" +
            "    x +%= 2;\n" +
            "    return x;\n" +
            "}\n");
        cs.ShouldContain("x += (byte)(2)"); // native compound op (value coerced to the LHS width)
    }

    // ---- Milestone P, part 2: saturating arithmetic (`+|` / `-|` / `*|` + compound) ----

    [Fact]
    public void Lowers_saturating_add_to_a_zigmath_call()
    {
        // `a +| b` has no native C# operator — it routes through the spliced `ZigMath.SatAdd<T>`
        // clamp helper. Both operands are already `u8`, so C# infers `T = byte` with no casts.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 200;\n" +
            "    const b: u8 = 100;\n" +
            "    const c: u8 = a +| b;\n" + // 300 -> 255
            "    return c;\n" +
            "}\n");
        cs.ShouldContain("ZigMath.SatAdd(a, b)");
    }

    [Fact]
    public void Lowers_saturating_mul_to_a_zigmath_call()
    {
        // `*|` is multiplicative-precedence; routes through `ZigMath.SatMul<T>`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 100;\n" +
            "    const b: u8 = 100;\n" +
            "    const c: u8 = a *| b;\n" + // 10000 -> 255
            "    return c;\n" +
            "}\n");
        cs.ShouldContain("ZigMath.SatMul(a, b)");
    }

    [Fact]
    public void Casts_a_literal_saturating_operand_so_csharp_infers_the_peer_type()
    {
        // `a +| 5` — `a` is `u8`, the literal yields to its concrete peer (`byte`); the literal is
        // cast so C# infers `T = byte` (and the runtime clamps at the u8 range, not int).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: u8 = 250;\n" +
            "    const c: u8 = a +| 5;\n" + // 255 (no saturation, but clamped at u8)
            "    return c;\n" +
            "}\n");
        cs.ShouldContain("ZigMath.SatAdd(a, (byte)5)");
    }

    [Fact]
    public void Lowers_saturating_at_int_width_without_redundant_casts()
    {
        // Two `i32` operands need no peer casts — C# infers `T = int` directly.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: i32 = 100;\n" +
            "    const b: i32 = 200;\n" +
            "    const c: i32 = a +| b;\n" +
            "    return @as(u8, @intCast(c - 258));\n" +
            "}\n");
        cs.ShouldContain("ZigMath.SatAdd(a, b)");
        cs.ShouldNotContain("ZigMath.SatAdd((int)a");
    }

    [Fact]
    public void Lowers_a_saturating_compound_assignment_to_a_zigmath_assignment()
    {
        // `x +|= y` has no native compound operator → desugars to `x = ZigMath.SatAdd(x, y)`.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var x: u8 = 200;\n" +
            "    x +|= 100;\n" + // 300 -> 255
            "    return x;\n" +
            "}\n");
        cs.ShouldContain("x = ZigMath.SatAdd(x,");
    }

    [Fact]
    public void Rejects_a_saturating_compound_assignment_to_a_side_effecting_target()
    {
        // The saturating compound desugar reads the lvalue twice; a target reached through a call
        // (`slot().*`) would double-eval it, so it is a clear deferred error rather than silent.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "var g: u8 = 0;\n" +
            "fn slot() *u8 { return &g; }\n" +
            "pub fn main() u8 {\n" +
            "    slot().* +|= 1;\n" +
            "    return 0;\n" +
            "}\n"));
        ex.Message.ShouldContain("side effects");
    }

    // ---- Milestone V: C↔Zig shared-heap interop -----------------------------

    /// <summary>Lower a MIXED .c + .zig translation-unit set into one C# program, like a real
    /// dotcc mixed compile (C group first, then the Zig group, sharing the name legalizer).</summary>
    private static string EmitMixed(string cBody, string zigBody)
    {
        var stem = Guid.NewGuid().ToString("N");
        var cPath = Path.Combine(Path.GetTempPath(), $"dotcc-mixed-{stem}.c");
        var zigPath = Path.Combine(Path.GetTempPath(), $"dotcc-mixed-{stem}.zig");
        File.WriteAllText(cPath, cBody);
        File.WriteAllText(zigPath, zigBody);
        try { return Compiler.EmitCSharp(new[] { cPath, zigPath }); }
        finally { File.Delete(cPath); File.Delete(zigPath); }
    }

    [Fact]
    public void Shares_the_c_heap_across_the_c_zig_seam()
    {
        // Zig allocates through std.heap.c_allocator (the C malloc/free heap) and a C function frees
        // it across the seam: the c_allocator default DEVIRTUALIZES to a direct Libc.malloc, the C
        // function is emitted in the same program, and the cross-language call binds by bare name.
        var cs = EmitMixed(
            "void take_and_free(int *p) { free(p); }\n",
            "const std = @import(\"std\");\n" +
            "extern fn take_and_free(p: [*c]c_int) void;\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const s = a.alloc(c_int, 4) catch return 1;\n" +
            "    s[0] = 42;\n" +
            "    const r: u8 = @intCast(s[0]);\n" +
            "    take_and_free(s.ptr);\n" +
            "    return r;\n" +
            "}\n");
        cs.ShouldContain("ZigAlloc.AllocCHeap<int>");   // c_allocator devirtualizes to the direct C heap
        cs.ShouldContain("take_and_free");              // the C function is in the same program
    }

    [Fact]
    public void Casts_a_create_catch_return_pointer_payload_to_the_element_pointer()
    {
        // `a.create(T) catch return X` carries the address as a nuint (a pointer can't be an
        // ErrUnion<T> generic arg); the catch-return unwrap must cast `.Value` back to `T*`, exactly
        // as the `try` path does. Regression pin for the Milestone V fix (was emitting a bare nuint →
        // CS0266). Mixed so the created object is read by C across the seam.
        var cs = EmitMixed(
            "int read_int(int *p) { return *p; }\n",
            "const std = @import(\"std\");\n" +
            "extern fn read_int(p: *c_int) c_int;\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const p = a.create(c_int) catch return 1;\n" +
            "    p.* = 30;\n" +
            "    const v = read_int(p);\n" +
            "    a.destroy(p);\n" +
            "    return @intCast(v + 12);\n" +
            "}\n");
        cs.ShouldContain("ZigAlloc.CreateCHeap<int>");   // create devirtualizes to the direct C heap
        cs.ShouldContain("(int*)");                      // the nuint payload is cast back to int*
    }

    [Fact]
    public void Passes_a_c_allocator_to_an_opaque_allocator_parameter()
    {
        // A Zig fn takes an opaque std.mem.Allocator param (→ indirect dispatch through the vtable);
        // main passes the c_allocator default, which materializes as ZigAlloc.CHeap(). The buffer it
        // allocates then crosses to a C function — proving the "pass the correct allocator" path.
        var cs = EmitMixed(
            "int sum_ints(int *p, int n) { int s = 0; for (int i = 0; i < n; i++) s += p[i]; return s; }\n",
            "const std = @import(\"std\");\n" +
            "extern fn sum_ints(p: [*c]c_int, n: c_int) c_int;\n" +
            "fn build(a: std.mem.Allocator, n: usize) ![]c_int {\n" +
            "    const s = try a.alloc(c_int, n);\n" +
            "    var i: usize = 0;\n" +
            "    while (i < n) : (i = i + 1) { s[i] = @intCast(i + 1); }\n" +
            "    return s;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    const a = std.heap.c_allocator;\n" +
            "    const s = build(a, 8) catch return 1;\n" +
            "    const total = sum_ints(s.ptr, 8);\n" +
            "    a.free(s);\n" +
            "    return @intCast(total + 6);\n" +
            "}\n");
        cs.ShouldContain("ZigAlloc.CHeap()");   // the default materializes for the opaque param
        cs.ShouldContain("sum_ints");
    }

    // ---- Milestone W, part 1a — function-pointer types + anyopaque -------

    [Fact]
    public void Lowers_fn_pointer_globals_incl_inferred_and_forward_ref()
    {
        // A fn-pointer GLOBAL — typed (`const h: *const fn (i32) i32 = &laterFn;`) or INFERRED
        // (`const alias = &laterFn;`) — is callable, and may forward-reference a function declared
        // LATER (functions are registered before globals). The inferred form needs `&fn` to be a bare
        // `CType.Func` fn-ptr value, not `Pointer(Func)`.
        var cs = EmitZig(
            "const handler: *const fn (i32) i32 = &laterFn;\n" +
            "const alias = &laterFn;\n" +
            "fn laterFn(x: i32) i32 { return x * 2; }\n" +
            "pub fn main() u8 { return @intCast(handler(10) + alias(11)); }\n"); // 20 + 22 = 42
        cs.ShouldContain("&laterFn");   // the function's address bound to the global
        cs.ShouldContain("handler(10)");
        cs.ShouldContain("alias(11)");  // the inferred fn-ptr global is callable
    }

    [Fact]
    public void Lowers_a_function_pointer_type_to_a_managed_delegate_pointer()
    {
        // `*const fn (a: i32, b: i32) i32` → a managed `delegate*<int, int, int>` (the vtable shape);
        // the pointer-to-function collapses to the bare Func, and binding a function emits `&add`.
        var cs = EmitZig(
            "fn add(a: i32, b: i32) i32 { return a + b; }\n" +
            "pub fn main() u8 {\n" +
            "    const op: *const fn (a: i32, b: i32) i32 = add;\n" +
            "    return @intCast(op(40, 2));\n" +
            "}\n");
        cs.ShouldContain("delegate*<int, int, int>");          // managed fn-ptr (NOT unmanaged[Cdecl])
        cs.ShouldNotContain("unmanaged[Cdecl]<int, int, int>"); // this fn-ptr is managed, not C-ABI
        cs.ShouldContain("&add");                               // the function decays to its address
    }

    [Fact]
    public void Honors_callconv_c_on_a_function_pointer_type_as_native_cdecl()
    {
        // `callconv(.c)` on a fn-POINTER type is a call-ABI annotation → the Func is marked
        // IsNativeCallConv, so the C# backend renders `delegate* unmanaged[Cdecl]<…>` instead of the
        // managed `delegate*<…>`. This is std's C-ABI fn-ptr typedef shape (e.g.
        // std/c/darwin/dispatch.zig: `*const fn (?*anyopaque) callconv(.c) void`). road-to-zig-std S9.
        // The type sits in PARAMETER position so it renders in the signature without a bind/coercion.
        var cs = EmitZig(
            "fn setCb(cb: *const fn (?*anyopaque) callconv(.c) void) void { _ = cb; }\n" +
            "pub fn main() u8 { return 0; }\n");
        // Scope the assertion to setCb's signature — the spliced Libc runtime legitimately contains
        // MANAGED `delegate*<void*, void>` fn-ptrs (threads/tss), so a bare ShouldNotContain would
        // false-positive on those.
        cs.ShouldContain("setCb(delegate* unmanaged[Cdecl]<void*, void> cb)");  // native C-ABI param
        cs.ShouldNotContain("setCb(delegate*<");                                // NOT the managed form
    }

    [Fact]
    public void Lowers_unnamed_param_and_error_returning_function_pointer_types()
    {
        // A fn-pointer type's params may be UNNAMED (`fn (i32, i32) i32`, the common Zig form) as well
        // as named; and the return may be an error union (`fn (i32) E!i32`) → the Func's Return is a
        // `CType.ErrorUnion` → `delegate*<int, ErrUnion<int>>`.
        var cs = EmitZig(
            "const E = error{ Bad };\n" +
            "fn add(a: i32, b: i32) i32 { return a + b; }\n" +
            "fn checked(x: i32) E!i32 { if (x < 0) return error.Bad; return x * 2; }\n" +
            "pub fn main() u8 {\n" +
            "    const f: *const fn (i32, i32) i32 = &add;\n" +   // unnamed params
            "    const h: *const fn (i32) E!i32 = &checked;\n" +  // !T-returning
            "    return @intCast(f(10, 5) + (h(4) catch 0));\n" + // 15 + 8 = 23
            "}\n");
        cs.ShouldContain("delegate*<int, int, int>");    // unnamed-param fn-ptr type
        cs.ShouldContain("ErrUnion<int>>");              // the !T return → delegate*<int, ErrUnion<int>>
    }

    [Fact]
    public void Calls_indirectly_through_a_function_pointer_value()
    {
        // Calling `op(40, 2)` where `op` is a fn-pointer LOCAL lowers to an indirect call — `op(40, 2)`
        // over the variable, not a by-name function call.
        var cs = EmitZig(
            "fn add(a: i32, b: i32) i32 { return a + b; }\n" +
            "pub fn main() u8 {\n" +
            "    const op: *const fn (a: i32, b: i32) i32 = add;\n" +
            "    return @intCast(op(40, 2));\n" +
            "}\n");
        cs.ShouldContain("op(40, 2)");   // the indirect call renders through the variable
    }

    [Fact]
    public void Lowers_anyopaque_pointer_to_void_pointer()
    {
        // `*anyopaque` → `void*` (C's type-erased pointer); `@ptrCast(@alignCast(ctx))` to a typed
        // pointer renders the `(int*)` cast.
        var cs = EmitZig(
            "fn f(ctx: *anyopaque) i32 { const p: *i32 = @ptrCast(@alignCast(ctx)); return p.*; }\n" +
            "pub fn main() u8 { var x: i32 = 42; return @intCast(f(&x)); }\n");
        cs.ShouldContain("void* ctx");   // the opaque parameter
        cs.ShouldContain("(int*)");      // the @ptrCast to the typed pointer
    }

    // ---- Milestone W, part 1b — a user-constructed custom std.mem.Allocator ----

    private const string CustomAllocator =
        "const std = @import(\"std\");\n" +
        "const Bump = struct { base: [*]u8, cap: usize, used: usize };\n" +
        "fn bumpAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {\n" +
        "    _ = alignment; _ = ret_addr;\n" +
        "    const self: *Bump = @ptrCast(@alignCast(ctx));\n" +
        "    if (self.used + len > self.cap) return null;\n" +
        "    const p = self.base + self.used; self.used += len; return p;\n" +
        "}\n" +
        "fn bumpResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {\n" +
        "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return false;\n" +
        "}\n" +
        "fn bumpRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {\n" +
        "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return null;\n" +
        "}\n" +
        "fn bumpFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {\n" +
        "    _ = ctx; _ = memory; _ = alignment; _ = ret_addr;\n" +
        "}\n" +
        "const bump_vtable = std.mem.Allocator.VTable{ .alloc = bumpAlloc, .resize = bumpResize, .remap = bumpRemap, .free = bumpFree };\n" +
        "pub fn main() u8 {\n" +
        "    var backing: [256]u8 = undefined;\n" +
        "    var state = Bump{ .base = backing[0..].ptr, .cap = backing.len, .used = 0 };\n" +
        "    const a = std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable };\n" +
        "    const buf = a.alloc(u8, 10) catch return 1;\n" +
        "    buf[0] = 42; a.free(buf); return buf[0];\n" +
        "}\n";

    [Fact]
    public void Lowers_a_user_vtable_literal_to_the_runtime_AllocatorVTable()
    {
        // `std.mem.Allocator.VTable{ .alloc = f, … }` → `new AllocatorVTable { alloc = &f, … }`,
        // and each function lowers with the real vtable signature (Alignment + Slice<byte>).
        var cs = EmitZig(CustomAllocator);
        cs.ShouldContain("new AllocatorVTable {");
        cs.ShouldContain("alloc = &bumpAlloc");
        cs.ShouldContain("free = &bumpFree");
        cs.ShouldContain("Alignment alignment");          // the modeled std.mem.Alignment param
        cs.ShouldContain("Slice<byte> memory");           // the []u8 param
    }

    [Fact]
    public void Constructs_a_runtime_allocator_from_ptr_and_vtable()
    {
        // `std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable }` → `new Allocator { Ctx = …,
        // Vtable = bump_vtable }` (the `&vtable` dropped to a by-value store), then the standard
        // `a.alloc` routes through the existing indirect vtable dispatch.
        var cs = EmitZig(CustomAllocator);
        cs.ShouldContain("new Allocator {");
        cs.ShouldContain("Vtable = bump_vtable");   // &vtable stored by value (no stray '&')
        cs.ShouldContain(".Alloc<byte>(");          // indirect dispatch through the user vtable
    }

    // ---- Milestone W, part 2 — a C lua_Alloc behind a Zig std.mem.Allocator ----

    [Fact]
    public void Bridges_a_c_lua_alloc_through_a_user_allocator_vtable()
    {
        // The deep bridge: a C `lua_Alloc`-shaped realloc allocator is imported via `extern fn` and
        // wrapped in a hand-written custom `std.mem.Allocator` whose 4-fn vtable calls the C fn-ptr
        // across the seam. dotcc lowers both files into one program; the C function binds by bare name
        // and the adapter's `alloc`/`free` invoke it (the `@ptrCast` of `?*anyopaque` → `?[*]u8`
        // renders `(byte*)`). No new lowering — pure composition of W1 + the Milestone V seam binding.
        var cs = EmitMixed(
            "#include <stdlib.h>\n#include <stddef.h>\n" +
            "void *lua_alloc(void *ud, void *ptr, size_t osize, size_t nsize) {\n" +
            "    (void)osize; (void)ud;\n" +
            "    if (nsize == 0) { free(ptr); return NULL; }\n" +
            "    return realloc(ptr, nsize);\n" +
            "}\n",
            "const std = @import(\"std\");\n" +
            "extern fn lua_alloc(ud: ?*anyopaque, ptr: ?*anyopaque, osize: usize, nsize: usize) ?*anyopaque;\n" +
            "fn luaAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = alignment; _ = ret_addr;\n" +
            "    return @ptrCast(lua_alloc(ctx, null, 0, len));\n" +
            "}\n" +
            "fn luaResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return false;\n" +
            "}\n" +
            "fn luaRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {\n" +
            "    _ = ctx; _ = memory; _ = alignment; _ = new_len; _ = ret_addr; return null;\n" +
            "}\n" +
            "fn luaFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {\n" +
            "    _ = alignment; _ = ret_addr; _ = lua_alloc(ctx, memory.ptr, memory.len, 0);\n" +
            "}\n" +
            "const lua_vtable = std.mem.Allocator.VTable{ .alloc = luaAlloc, .resize = luaResize, .remap = luaRemap, .free = luaFree };\n" +
            "pub fn main() u8 {\n" +
            "    var bytes: usize = 0;\n" +
            "    const a = std.mem.Allocator{ .ptr = &bytes, .vtable = &lua_vtable };\n" +
            "    const buf = a.alloc(u8, 4) catch return 1;\n" +
            "    buf[0] = 42; a.free(buf); return buf[0];\n" +
            "}\n");
        cs.ShouldContain("lua_alloc(void* ud");           // the C allocator is in the same program
        cs.ShouldContain("(byte*)lua_alloc(");            // the adapter's @ptrCast-ed alloc call
        cs.ShouldContain("new Allocator {");              // the custom allocator is constructed
        cs.ShouldContain(".Alloc<byte>(");                // dispatch routes through the user vtable
    }

    // ---- Milestone X, part 1 — @errorName + the un-erased code→name table ----

    [Fact]
    public void Lowers_errorName_to_a_code_to_name_table_lookup()
    {
        // `@errorName(e)` → a call to the emitted `__zigErrorName(code)` helper returning a
        // `ConstSlice<byte>`; the helper's code→name table carries each registered error name as
        // RVA-pinned UTF-8 bytes (`L("Foo"u8)`), un-erasing the otherwise-flat error code.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const n = @errorName(error.Foo);\n" +
            "    return @intCast(n.len);\n" +
            "}\n");
        cs.ShouldContain("__zigErrorName(");                  // the builtin lowers to the table lookup
        cs.ShouldContain("ConstSlice<byte> __zigErrorName");  // the table helper is emitted
        cs.ShouldContain("L(\"Foo\"u8)");                     // the name is RVA-pinned in the table
    }

    [Fact]
    public void Does_not_emit_the_error_name_table_when_no_error_is_named()
    {
        // The `__zigErrorName` helper is emitted only when the program names ≥1 error, so an
        // error-free program stays clean (no dead table).
        var cs = EmitZig("pub fn main() u8 { return 42; }\n");
        cs.ShouldNotContain("__zigErrorName");
    }

    // ---- Milestone X, part 2 — E.member (set-qualified error reference) ----

    [Fact]
    public void Resolves_a_set_qualified_error_member_like_the_bare_error_form()
    {
        // `MyError.Boom` (set-qualified) lowers to the same flat error code as the bare `error.Boom`
        // (membership erased) — as a compared value AND in `return` position. The set name is fully
        // erased (no `MyError`/`.Boom` member access leaks into the emit); the `!u8` fn returns an
        // error union.
        var cs = EmitZig(
            "const MyError = error{ Boom, Fizz };\n" +
            "fn boom() MyError!u8 { return MyError.Boom; }\n" +
            "pub fn main() u8 {\n" +
            "    var acc: u8 = 0;\n" +
            "    if (MyError.Boom == error.Boom) acc += 1;\n" +
            "    _ = boom() catch 0;\n" +
            "    return acc;\n" +
            "}\n");
        cs.ShouldContain("ErrUnion<byte>");   // boom()'s !u8 error-union return lowered
        cs.ShouldNotContain("MyError");       // the set name is fully erased
        cs.ShouldNotContain(".Boom");         // E.member resolved to a flat code, not a member access
    }

    // ---- Milestone X, part 3 — error-set membership checking (reject illegal programs) ----

    [Fact]
    public void Rejects_returning_an_error_outside_the_functions_declared_set()
    {
        // `fn f() error{A}!u8 { return error.B; }` — B is not in the declared set, so a good compiler
        // rejects it (real zig: "error.B not a member of destination error set"). dotcc keeps the flat
        // runtime code but now checks membership at the return.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const E = error{ A };\n" +
            "fn f() E!u8 { return error.B; }\n" +
            "pub fn main() u8 { return f() catch 0; }\n"));
        ex.Message.ShouldContain("'B'");
        ex.Message.ShouldContain("'E'");
    }

    [Fact]
    public void Rejects_a_set_qualified_member_not_declared_in_the_set()
    {
        // `E.Nope` where Nope ∉ E — an illegal set-qualified reference (closes the X2 leniency cut).
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const E = error{ A, B };\n" +
            "pub fn main() u8 { const e = E.Nope; _ = e; return 0; }\n"));
        ex.Message.ShouldContain("'Nope'");
        ex.Message.ShouldContain("'E'");
    }

    [Fact]
    public void Accepts_in_set_errors_and_leaves_an_inferred_set_unchecked()
    {
        // The legal side: every returned error is a member of the declared set (bare AND
        // set-qualified), and an inferred `!T` stays UNCONSTRAINED (any error allowed — real zig
        // infers the set). Both lower to error unions with no false rejection.
        var cs = EmitZig(
            "const E = error{ A, B };\n" +
            "fn pick(x: u8) E!u8 { if (x == 0) return error.A; if (x == 1) return E.B; return x; }\n" +
            "fn any(x: u8) !u8 { if (x == 0) return error.Anything; return x; }\n" +
            "pub fn main() u8 { const a = pick(7) catch 0; const b = any(7) catch 0; return a + b; }\n");
        cs.ShouldContain("ErrUnion<byte>");
    }

    // ---- Milestone X, part 3b — an error set as a plain VALUE type + exhaustive switch-expr ----

    [Fact]
    public void Lowers_an_error_set_used_as_a_value_type_to_the_erased_ushort_code()
    {
        // An error set used as a plain param / non-`!T` return / local type denotes the error VALUE
        // itself — lowered to the flat erased `ushort` code (NOT an `ErrUnion<T>` union), with the
        // set name fully erased. `worst()`'s `MathError` return and `weight`'s `MathError` param both
        // render bare `ushort`; previously this was the bogus rejection "zig type 'MathError' …
        // (slice)". (The runtime block always DEFINES `ErrUnion<T>`, so check the signatures, not a
        // bare "ErrUnion" substring.)
        var cs = EmitZig(
            "const MathError = error{ DivByZero, Overflow };\n" +
            "fn worst() MathError { return MathError.Overflow; }\n" +
            "fn weight(e: MathError) u8 { _ = e; return 1; }\n" +
            "pub fn main() u8 { const e: MathError = worst(); return weight(e); }\n");
        cs.ShouldContain("ushort worst()");      // a non-`!T` error return → the bare flat code
        cs.ShouldContain("weight(ushort e)");    // an error-set parameter → the bare flat code
        cs.ShouldNotContain("ErrUnion<ushort>"); // NOT an error union over the code
        cs.ShouldNotContain("MathError");        // the set name is fully erased
    }

    [Fact]
    public void Injects_an_implicit_default_for_an_exhaustive_error_switch_expression()
    {
        // A switch EXPRESSION over an error set with no `else` is exhaustive in zig but unprovable in
        // C# (CS8509 "not all values covered") over the erased `ushort`. dotcc collapses the LAST arm
        // to the `_` default so the emit stays warning-clean — semantics-preserving for the
        // exhaustive program (only that arm's values reach it).
        var cs = EmitZig(
            "const E = error{ A, B, C };\n" +
            "fn weight(e: E) u8 { return switch (e) { error.A => 10, error.B => 20, error.C => 5 }; }\n" +
            "pub fn main() u8 { return weight(error.B); }\n");
        cs.ShouldContain("switch {"); // a C# switch EXPRESSION
        cs.ShouldContain("_ =>");      // the injected implicit default arm (the collapsed last prong)
    }

    [Fact]
    public void Lowers_a_block_bodied_switch_expression_prong_as_a_statement_temp_fill()
    {
        // A switch EXPRESSION with a labeled value-block prong (`0 => blk: {…; break :blk v;}`) can't
        // be a C# switch-expression (whose arms must be pure expressions). At a `const`/`var`/`return`/
        // assignment RHS, dotcc lowers the whole switch as a STATEMENT filling a result temp (`__vcf`),
        // each prong assigning it; the labeled block lowers via its own temp + `goto …_end`. (Y, part 1.)
        var cs = EmitZig(
            "fn classify(n: i32) i32 {\n" +
            "    const label = switch (n) {\n" +
            "        0 => blk: { const h: i32 = 100; break :blk h + 1; },\n" +
            "        1, 2 => 20,\n" +
            "        else => 5,\n" +
            "    };\n" +
            "    return label;\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(classify(1)); }\n");
        cs.ShouldContain("__vcf");        // the value-control-flow result temp
        cs.ShouldContain("goto __blk");   // the labeled value-block prong lowered to a temp + goto
        cs.ShouldContain("default:");     // the `else` prong → the switch default section
    }

    [Fact]
    public void Lowers_a_block_bodied_if_expression_as_a_statement_temp_fill()
    {
        // An if EXPRESSION with a labeled value-block branch (`if (c) blk: {…} else v`) can't be a C#
        // ternary (whose operands must be pure expressions). At a statement RHS it lowers as an `if`
        // statement filling a result temp. (Milestone Y, part 1.)
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const x = if (true) blk: { const b: i32 = 42; break :blk b; } else 0;\n" +
            "    return @intCast(x);\n" +
            "}\n");
        cs.ShouldContain("__vcf");      // the result temp
        cs.ShouldContain("goto __blk"); // the labeled value-block branch
    }

    [Fact]
    public void Rejects_a_block_bodied_value_switch_in_an_error_union_return()
    {
        // The result-temp `return` of a value-position switch/if is deferred in an error-union (`!T`)
        // function — the `ErrUnion<T>` wrapping would need to apply to the temp (mirrors the bare
        // labeled-block `!T` return cut). A clear deferred error, not a silent unwrapped return.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "fn f(n: i32) !i32 {\n" +
            "    return switch (n) {\n" +
            "        0 => blk: { break :blk 1; },\n" +
            "        else => 2,\n" +
            "    };\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(f(0) catch 9); }\n"));
        ex.Message.ShouldContain("error-union");
    }

    [Fact]
    public void Lowers_a_while_else_value_loop_with_a_break_value()
    {
        // Milestone Y, part 2 — a `while (cond) { … break v; } else d` in value position yields `v`
        // on break, `d` on normal completion. Lowered as a STATEMENT filling a result temp `__lv`: a
        // `break v` → `__lv = v; goto __lv_end;` (skipping the `else`), and the `else` value assigned
        // after the loop on natural completion.
        var cs = EmitZig(
            "fn f() i32 {\n" +
            "    var i: i32 = 0;\n" +
            "    return while (i < 50) {\n" +
            "        i = i + 1;\n" +
            "        if (i == 20) break i;\n" +
            "    } else 0;\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(f()); }\n");
        cs.ShouldContain("__lv");          // the value-loop result temp
        cs.ShouldContain("goto __lv");     // a `break v` → assign + goto end
        cs.ShouldContain("__lv0_end:");    // the end label (emitted because a break targets it)
    }

    [Fact]
    public void Lowers_a_for_else_value_loop_over_a_slice()
    {
        // A `for (slice) |x| { … break x; } else d` — the search idiom — reuses the for-over-slice
        // loop, the value target threaded so an inner `break x` fills `__lv`. (Milestone Y, part 2.)
        var cs = EmitZig(
            "fn first_big(xs: []const i32) i32 {\n" +
            "    return for (xs) |x| {\n" +
            "        if (x > 5) break x;\n" +
            "    } else -1;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var arr = [_]i32{ 1, 9 };\n" +
            "    return @intCast(first_big(arr[0..]));\n" +
            "}\n");
        cs.ShouldContain("__lv");      // value-loop temp
        cs.ShouldContain("goto __lv"); // break-value fills it
        cs.ShouldContain(".Len");      // a real for-over-slice loop, not a switch/ternary
    }

    [Fact]
    public void Lowers_a_labeled_while_else_value_loop_with_an_outer_break()
    {
        // A labeled value loop `outer: while (…) { … break :outer v; } else d` — the `break :outer v`
        // from an inner (statement) loop resolves to the outer value loop's temp + end label, so the
        // goto jumps out of both loops. (Milestone Y, part 2.)
        var cs = EmitZig(
            "fn f() i32 {\n" +
            "    var i: i32 = 0;\n" +
            "    return outer: while (i < 5) {\n" +
            "        var j: i32 = 0;\n" +
            "        while (j < 5) {\n" +
            "            if (i == 2 and j == 3) break :outer 15;\n" +
            "            j = j + 1;\n" +
            "        }\n" +
            "        i = i + 1;\n" +
            "    } else 0;\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(f()); }\n");
        cs.ShouldContain("__lv");      // the outer value loop's temp
        cs.ShouldContain("= 15;");     // the `break :outer 15` assigns it
        cs.ShouldContain("goto __lv"); // and jumps to the end label, out of both loops
    }

    [Fact]
    public void Omits_the_end_label_for_an_else_only_value_loop()
    {
        // A value loop with NO `break` (only the `else` path) never jumps to the end label — so it is
        // omitted, avoiding a C# unreferenced-label warning (CS0164). (Milestone Y, part 2.)
        var cs = EmitZig(
            "fn f() i32 {\n" +
            "    var i: i32 = 0;\n" +
            "    return while (i < 3) { i = i + 1; } else 42;\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(f()); }\n");
        cs.ShouldContain("__lv");          // the value temp + else assignment still emitted
        cs.ShouldNotContain("__lv0_end:"); // but no end label (no break targets it)
    }

    [Fact]
    public void Binds_a_multi_variant_union_capture_via_the_first_variants_field()
    {
        // Milestone Z — a multi-variant capture prong `.circle, .square => |r|` (both i32) binds `r`
        // to the FIRST variant's payload field; in the explicit-layout payload union every variant
        // overlaps at offset 0, so reading `.circle` aliases whichever (`.circle`/`.square`) matched.
        var cs = EmitZig(
            "const Shape = union(enum) { circle: i32, square: i32, name: u8 };\n" +
            "fn area(s: Shape) i32 {\n" +
            "    switch (s) {\n" +
            "        .circle, .square => |r| { return r * 2; },\n" +
            "        .name => |c| { return @as(i32, c); },\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(area(Shape{ .circle = 21 })); }\n");
        cs.ShouldContain("Shape_Tag.circle"); // both variants are section labels
        cs.ShouldContain("Shape_Tag.square");
        cs.ShouldContain("__payload.circle"); // bound via the first variant's (aliasing) field
    }

    [Fact]
    public void Rejects_a_multi_variant_union_capture_with_differing_payload_types()
    {
        // A capture binds to one `|x|`, so the listed variants must share a payload type. `.circle`
        // (i32) and `.name` (u8) differ → a clear error, not a silent mistyped read.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "const Shape = union(enum) { circle: i32, name: u8 };\n" +
            "fn f(s: Shape) i32 {\n" +
            "    switch (s) {\n" +
            "        .circle, .name => |x| { return @as(i32, x); },\n" +
            "    }\n" +
            "}\n" +
            "pub fn main() u8 { return @intCast(f(Shape{ .circle = 1 })); }\n"));
        ex.Message.ShouldContain("same payload type");
    }

    [Fact]
    public void Lowers_a_for_slice_indexed_byref_capture()
    {
        // Milestone Z — `for (s, 0..) |*e, i|` binds BOTH a by-reference element (`*T`, so `e.* = …`
        // writes through to the slice) AND the usize index. Reuses LowerForSlice with byRef + index.
        var cs = EmitZig(
            "fn bump(xs: []i32) void {\n" +
            "    for (xs, 0..) |*e, i| { e.* = e.* + @as(i32, @intCast(i)); }\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    var arr = [_]i32{ 1, 2 };\n" +
            "    bump(arr[0..]);\n" +
            "    return @intCast(arr[1]);\n" +
            "}\n");
        cs.ShouldContain("* e = &");  // a by-reference element pointer into the slice
        cs.ShouldContain("*e =");      // writes through `e.*`
    }

    [Fact]
    public void Materializes_a_nonzero_sentinel_in_an_array_literal()
    {
        // Milestone Z — a `[N:s]T` array literal with a NON-ZERO sentinel appends `s` (not `0`) as the
        // trailing slot: `stackalloc i32[]{ 10, 11, 12, 9 }` (N+1 slots; the symbol's type stays the
        // N-element array, so `.len`/slicing exclude the sentinel).
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const a: [3:9]i32 = .{ 10, 11, 12 };\n" +
            "    return @intCast(a[3]);\n" +
            "}\n");
        cs.ShouldContain("10, 11, 12, 9"); // the non-zero sentinel appended to the stackalloc literal
    }

    [Fact]
    public void Writes_a_nonzero_sentinel_into_an_undefined_sentinel_array()
    {
        // For an `undefined` `[N:s]T` with a non-zero sentinel, C#'s zero-fill leaves the trailing
        // slot at 0, so dotcc writes the actual sentinel explicitly: `i32* b = stackalloc i32[3];
        // b[2] = 7;`. (dotcc defines the sentinel of an `undefined` array — like its zero-fill of
        // `undefined` generally; real zig leaves it undefined, so this is NOT oracle-checked.)
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    var b: [2:7]i32 = undefined;\n" +
            "    b[0] = 1; b[1] = 1;\n" +
            "    return @intCast(b[2] + b[0] + b[1]);\n" +
            "}\n");
        cs.ShouldContain("stackalloc int[3]"); // N+1 slots reserved
        cs.ShouldContain("] = 7;");             // the non-zero sentinel written into the trailing slot
    }

    [Fact]
    public void Accepts_a_trailing_comma_in_a_multiline_param_list()
    {
        // road-to-zig-std S9 — the idiomatic multi-line signature ends the last param with a comma
        // (`b: i32,\n)`); 36 std files fail first on it. The trailing comma is cosmetic: the fn
        // lowers exactly as the one-line, no-trailing-comma form.
        var cs = EmitZig(
            "fn add(\n" +
            "    a: i32,\n" +
            "    b: i32,\n" +
            ") i32 {\n" +
            "    return a + b;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(add(2, 3));\n" +
            "}\n");
        cs.ShouldContain("add(int a, int b)");
    }

    [Fact]
    public void Accepts_a_trailing_comma_in_a_fn_type_param_list()
    {
        // Same trailing-comma allowance on a fn-POINTER-type signature (FnTypeParams) — a `*const fn`
        // parameter whose own multi-line param list ends with a comma. Grammar-accepted; the callee
        // signature is unaffected by the cosmetic comma.
        var cs = EmitZig(
            "fn dbl(x: i32) i32 { return x * 2; }\n" +
            "fn apply(f: *const fn (\n" +
            "    x: i32,\n" +
            ") i32, v: i32) i32 {\n" +
            "    return f(v);\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return @intCast(apply(&dbl, 5));\n" +
            "}\n");
        cs.ShouldContain("apply(");
    }

    [Fact]
    public void Drops_a_named_and_anonymous_test_block()
    {
        // road-to-zig-std S9 — `test "…" {}` / `test {}` blocks are analysis-only; dotcc parses and
        // DROPS them (a normal build never runs tests). The block bodies are never lowered, yet the
        // program compiles and only `main` is emitted — the test-name string never reaches the output.
        var cs = EmitZig(
            "test \"addition\" {\n" +
            "    const x = 1 + 1;\n" +
            "    _ = x;\n" +
            "}\n" +
            "test {\n" +
            "    const w = 9;\n" +
            "    _ = w;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return 7;\n" +
            "}\n");
        cs.ShouldContain("main");
        cs.ShouldNotContain("addition"); // the test name string is dropped, not emitted
    }

    [Fact]
    public void Drops_a_bare_ident_test_block()
    {
        // The bare-identifier test-name form (`test encode {`) that std uses (e.g. `test decode {`).
        var cs = EmitZig(
            "test encode {\n" +
            "    const y: i32 = 2;\n" +
            "    _ = y;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return 3;\n" +
            "}\n");
        cs.ShouldContain("main");
    }

    [Fact]
    public void Drops_a_container_level_comptime_block()
    {
        // A container-level `comptime {}` block is analysis-only in std — parsed and DROPPED (the
        // comptime engine that would evaluate its guards is S4–S7). `main` still compiles and runs.
        var cs = EmitZig(
            "comptime {\n" +
            "    const z: i32 = 4;\n" +
            "    _ = z;\n" +
            "}\n" +
            "pub fn main() u8 {\n" +
            "    return 5;\n" +
            "}\n");
        cs.ShouldContain("main");
    }

    [Fact]
    public void Materializes_an_omitted_struct_field_default()
    {
        // road-to-zig-std S9 — a struct field with a default (`n: i32 = 7`) is materialized when a
        // `.{…}` literal OMITS it; a written field (`m`) overrides. This is the NON-ZERO case where
        // C#'s zero-init would be wrong: the literal `.{ .m = 3 }` must fill `n = 7`, so s.n+s.m = 10.
        var cs = EmitZig(
            "const S = struct { n: i32 = 7, m: i32 };\n" +
            "pub fn main() u8 {\n" +
            "    const s: S = .{ .m = 3 };\n" +
            "    return @intCast(s.n + s.m);\n" +
            "}\n");
        cs.ShouldContain("n = 7"); // the default materialized into the struct init, not left C#-zero
    }

    [Fact]
    public void Parses_but_defers_a_file_as_struct_top_level_field()
    {
        // road-to-zig-std S9 — a Zig file is an implicit struct, so it may carry container fields at
        // the top level (`bit_len: usize = 0,`). We now PARSE this (the largest std parse bucket), but
        // reifying the whole file as an instantiable struct type is the S1 lift, so lowering must fail
        // LOUDLY rather than silently drop the field. Parse-accept + honest deferral, not a bad emit.
        var ex = Should.Throw<CompileException>(() => EmitZig(
            "bit_len: usize = 0,\n" +
            "pub fn main() u8 {\n    return 0;\n}\n"));
        ex.Message.ShouldContain("file-as-struct");
    }

    [Fact]
    public void Lexes_a_quoted_identifier_and_mangles_it_to_a_csharp_name()
    {
        // road-to-zig-std S9 — `@"…"` is Zig's quoted identifier (the reserved-word / arbitrary-string
        // escape hatch). It lexes to a plain IDENT; Tok strips the `@"`/`"` and mangles any char that
        // isn't C#-identifier-legal to `_`, so `@"a-b"` becomes `a_b` — used consistently at the
        // declaration and at the use, so the two still refer to the same local.
        var cs = EmitZig(
            "pub fn main() u8 {\n" +
            "    const @\"a-b\": u8 = 42;\n" +
            "    return @\"a-b\";\n}\n");
        cs.ShouldContain("a_b");
    }
}

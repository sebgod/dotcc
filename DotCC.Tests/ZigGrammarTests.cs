#nullable enable

using LALR.CC;
using LALR.CC.LexicalGrammar;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Parse-acceptance regression for the production Zig grammar
/// (<c>DotCC.Lib/zig.lalr.yaml</c> → generated <c>DotCC.Zig</c>), behind the
/// <c>IFrontend</c> seam. Slice 1 = the C-shaped value/type core. The grammar
/// generating at all already proves it's LALR(1)-clean (a conflict aborts the
/// build); these tests pin ACCEPTANCE (real Zig parses) and faithful REJECTION
/// (e.g. the non-associative <c>a &lt; b &lt; c</c>) through the real
/// <c>BytesLexer → Parser.ParseInput</c> pipeline with the generated
/// <c>IdentityVisitor</c>. The standalone spike lives in SharpAstro/LALR.CC
/// (<c>examples/Zig</c>); this is the dotcc-side copy the frontend grows on.
/// </summary>
[Collection("ZigGrammar")]
public sealed class ZigGrammarTests
{
    private static bool TryParse(string src)
    {
        try
        {
            var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
            using var lexer = BytesLexer.FromString(src, Zig.BuildLexer());
            using var tokens = new SyncLATokenIterator(lexer);
            parser.ParseInput(tokens);
            return true;
        }
        catch (ParseErrorException) { return false; }
    }

    [Theory]
    [InlineData("fn add(a: i32, b: i32) i32 { return a + b * 2; }")]
    [InlineData("const Pi = 3.14;\npub fn main() void {\n    const x = Pi + 1.0;\n    return;\n}")]
    [InlineData("fn f(p: *u8, q: ?T) void {\n    const x = p[0];\n    const y = foo.bar(p, 3) + @intCast(x);\n    var z = a.b.c;\n    z = y;\n    return;\n}")]
    [InlineData("fn g(n: i32) !i32 {\n    var i = 0;\n    while (i < n) {\n        if (i == 3) { return i; } else { i = i + 1; }\n    }\n    return n;\n}")]
    [InlineData("fn h(p: *T) T { return p.*.field.?; }")]
    // slice 2a — if-expression in value position (init / return / assignment RHS)
    [InlineData("fn f(c: bool) i32 { const x = if (c) 1 else 2; return x; }")]
    [InlineData("fn g(c: bool) i32 { return if (c) 10 else 20; }")]
    [InlineData("fn k(c: bool) i32 { var x = 0; x = if (c) 1 else 2; return x; }")]
    [InlineData("fn n(a: bool, b: bool) i32 { return if (a) 1 else if (b) 2 else 3; }")] // nested
    [InlineData("fn m(c: bool) i32 { return if (c) 1 else 2 + 3; }")] // greedy else: else = (2 + 3)
    // GH #129: an `if` as the right operand of `orelse` / `catch`, without parentheses
    [InlineData("fn f(o: ?u8, g: bool) u8 { return o orelse if (g) 5 else 6; }")]
    [InlineData("fn f(o: E!u8, g: bool) u8 { return o catch if (g) 5 else 6; }")]
    [InlineData("fn f(o: ?u8, g: bool) u8 { const v: u8 = o orelse if (g) 5 else return 6; return v; }")]
    [InlineData("fn f(o: ?u8, p: ?u8, g: bool) u8 { return o orelse if (g) 5 else p orelse 6; }")]
    [InlineData("const m: usize = a orelse (b orelse if (c) @compileError(\"x\") else @compileError(\"y\"));")]
    [InlineData("fn f(o: ?u8, p: E!u8) S { return .{ .n = o orelse if (p) |n| n + 1 else |_| 0 }; }")]
    // GH #282: a tagged enum, with methods, as a type arm
    [InlineData("const P = if (w) enum(u32) { a = 1, _ } else enum(u16) { b = 2, pub fn f(p: @This()) u16 { return @intFromEnum(p); } };")]
    // GH #283: jumps as arms of a value `if`, and an `if` whose arms both jump as an `orelse` fallback
    [InlineData("fn f(xs: []const u8) u8 { var t: u8 = 0; for (xs) |x| { const y = if (x < 100) x else break; t += y; } return t; }")]
    [InlineData("fn f(xs: []const u8) u8 { var t: u8 = 0; for (xs) |x| { t += if (x == 0) continue else x; } return t; }")]
    [InlineData("fn f(o: ?u8, c: bool) !?u8 { const v: ?u8 = blk: { const q = o orelse if (c) return error.U else break :blk null; break :blk q + 1; }; return v; }")]
    [InlineData("fn f(o: ?u8, d: u8) u8 { const v = blk: { const q = o orelse break :blk d; break :blk q + 1; }; return v; }")]
    [InlineData("fn f(xs: []const u8, d: u8) u8 { const v = blk: { for (xs) |x| { const y = if (x < 9) x else break :blk d; _ = y; } break :blk 0; }; return v; }")]
    // GH #285: enum members named after primitive values, with a value, and qualified access to them
    [InlineData("const P = enum(u8) { null = 0, all_ones = 255, _ };")]
    [InlineData("const V = enum(u8) { none = 0, false = 1 + @intFromEnum(C.false), true = 2, _ };")]
    [InlineData("const R = enum(u8) { undefined = 7, same_value };")]
    [InlineData("const R = enum { undefined, other };")]
    [InlineData("const t = Constant.true; const n = E.null; const u = E.undefined;")]
    // GH #286: a labeled switch's `continue :label operand`, as a statement, a prong body, a value `if` arm and a fallback
    [InlineData("fn f(s: State) void { state: switch (s) { .a => { continue :state .b; }, .b => {} } }")]
    [InlineData("fn f(s: State) u8 { return sw: switch (s) { .a => continue :sw .b, .b => 1 }; }")]
    [InlineData("fn f(s: State, v: u8) u8 { return r: switch (s) { .a => if (v < 5) 0 else continue :r .b, .b => 1 }; }")]
    [InlineData("fn f(s: State, o: ?State) void { sw: switch (s) { .a => { const n = o orelse continue :sw .b; _ = n; }, .b => {} } }")]
    public void Accepts_real_zig(string src) => TryParse(src).ShouldBeTrue();

    [Theory]
    [InlineData("fn bad(a: i32) i32 { return a < a < a; }")] // compare is non-associative
    [InlineData("fn h() void { + }")]                         // stray operator as a statement
    public void Rejects_invalid(string src) => TryParse(src).ShouldBeFalse();

    /// <summary>Parse <paramref name="src"/> and return the first node (pre-order, left to right) whose record is a
    /// <typeparamref name="T"/>. The records are generated with <c>Arg0..ArgN</c> item properties, read reflectively
    /// here so a shape pin can name the node it is about without spelling the path to it.</summary>
    private static T FirstNode<T>(string src) where T : class
    {
        var parser = Zig.BuildParser(Zig.IdentityVisitor.Instance);
        using var lexer = BytesLexer.FromString(src, Zig.BuildLexer());
        using var tokens = new SyncLATokenIterator(lexer);
        var root = parser.ParseInput(tokens, debugger: null, trimReductions: true);
        return Find<T>(root) ?? throw new ShouldAssertException($"no {typeof(T).Name} in the parse of: {src}");
    }

    private static T? Find<T>(Item? item) where T : class
    {
        if (item?.Content is null) { return null; }
        if (item.Content is T hit) { return hit; }
        foreach (var prop in item.Content.GetType().GetProperties())
        {
            if (prop.PropertyType == typeof(Item) && Find<T>((Item?)prop.GetValue(item.Content)) is { } found) { return found; }
        }
        return null;
    }

    /// <summary>
    /// zig-grammar-peg P1: an `if` expression is an operand anywhere (zig's IfExpr is a PrimaryExpr), and its else arm
    /// takes everything to its right, as zig's greedy PEG choice does. These pin the parse SHAPE, so a conflict the table
    /// builder settled the wrong way (the hazard the open/closed cascade exists to avoid) fails here, not silently.
    /// </summary>
    [Fact]
    public void An_if_expression_is_a_right_operand_and_its_else_arm_is_greedy()
    {
        // `1 + if (c) 2 else 3 + 4` is `1 + (if (c) 2 else (3 + 4))`.
        var add = FirstNode<Zig.Add>("fn f(c: bool) i32 { return 1 + if (c) 2 else 3 + 4; }");
        add.Arg0.Content.ShouldBeOfType<Zig.IntLit>();
        var ie = add.Arg2.Content.ShouldBeOfType<Zig.IfExpr>();
        ie.Arg6.Content.ShouldBeOfType<Zig.Add>();
    }

    [Fact]
    public void Operator_precedence_holds_inside_an_if_arm()
    {
        // In the else arm, `b + d % e` is `b + (d % e)`: the arm's own cascade, not a precedence decision.
        var ie = FirstNode<Zig.IfExpr>("fn f(c: bool, b: i32, d: i32, e: i32) i32 { return if (c) 0 else b + d % e; }");
        var arm = ie.Arg6.Content.ShouldBeOfType<Zig.Add>();
        arm.Arg2.Content.ShouldBeOfType<Zig.ModOp>();
        // And outside any `if`, unchanged.
        FirstNode<Zig.Add>("fn g(b: i32, d: i32, e: i32) i32 { return b + d % e; }").Arg2.Content.ShouldBeOfType<Zig.ModOp>();
    }

    [Fact]
    public void A_jump_is_an_expression_and_a_returned_value_is_greedy()
    {
        // zig-grammar-peg P1b: `return` / `break` / `continue` are PrimaryExprs. `x orelse return y orelse 2` returns
        // `y orelse 2`, and the fallback is the ordinary `orelse` with a ReturnExpr operand.
        var oe = FirstNode<Zig.OrElse>("fn f(x: ?u8, y: ?u8) u8 { return x orelse return y orelse 2; }");
        oe.Arg2.Content.ShouldBeOfType<Zig.ReturnExpr>().Arg1.Content.ShouldBeOfType<Zig.OrElse>();
        // A labeled break takes its label and value; a bare one is a value-less jump.
        FirstNode<Zig.FbBreakLabelValue>("fn g(o: ?u8) u8 { const v = blk: { break :blk o orelse break :blk 3; }; return v; }")
            .Arg3.Content.ShouldNotBeNull();
        FirstNode<Zig.IfExpr>("fn h(o: ?u8, c: bool) void { while (true) { _ = o orelse if (c) break else continue; } }")
            .Arg4.Content.ShouldBeOfType<Zig.FbBreak>();
    }

    [Fact]
    public void A_statement_if_takes_an_error_capture_without_a_then_capture()
    {
        // zig-grammar-peg P2 (std's Io/Dir.zig): the success value is discarded, the error bound.
        var s = FirstNode<Zig.StmtIfElseErr>("fn f() void { if (g()) { return; } else |err| switch (err) { else => {} } }");
        s.Arg4.Content.ShouldBeOfType<Zig.Block>();
    }

    [Fact]
    public void An_if_may_drop_its_else_and_take_block_arms()
    {
        // zig-grammar-peg P3a: zig's IfExpr has an optional `else`, and a block is a PrimaryExpr.
        var both = FirstNode<Zig.IfExpr>("fn f(c: bool) void { switch (0) { 0 => if (c) { g(); } else { h(); }, else => {} } }");
        both.Arg4.Content.ShouldBeOfType<Zig.Block>();
        both.Arg6.Content.ShouldBeOfType<Zig.Block>();
        FirstNode<Zig.IfExprNoElse>("fn f(c: bool) bool { switch (0) { 0 => if (c) return true, else => {} } return false; }")
            .Arg4.Content.ShouldBeOfType<Zig.ReturnExpr>();
        // The `;` after a block-armed `if` written as an expression statement is an empty statement.
        TryParse("fn f(o: ?u8) void { if (o) |p| if (p > 0) { g(); }; }").ShouldBeTrue();
        // A dangling `else` still binds to the nearest `if`.
        FirstNode<Zig.IfExpr>("fn f(a: bool, b: bool) u8 { return if (a) if (b) 1 else 2 else 3; }")
            .Arg4.Content.ShouldBeOfType<Zig.IfExpr>();
    }

    [Fact]
    public void A_switch_is_an_operand_anywhere()
    {
        // zig-grammar-peg P3: `3 * switch (k) {…} + 1` is `(3 * switch …) + 1` (a closed primary), and a statement-start
        // `switch` is still the statement.
        var add = FirstNode<Zig.Add>("fn f(k: u8) u32 { return 3 * switch (k) { else => 1 } + 1; }");
        add.Arg0.Content.ShouldBeOfType<Zig.Mul>().Arg2.Content.ShouldBeOfType<Zig.SwitchExpr>();
        FirstNode<Zig.CmpLt>("fn g(x: i32) bool { return switch (x) { else => x } < 0; }").Arg0.Content.ShouldBeOfType<Zig.SwitchExpr>();
        TryParse("fn h(x: u8) void { switch (x) { else => {} } *p = 1; }").ShouldBeTrue();
    }

    [Fact]
    public void An_inline_struct_is_a_type_anywhere()
    {
        // `struct { … }` is a Type, so it nests under `[N]` / `*` / `?`, types an array literal, and is an argument.
        FirstNode<Zig.TyArray>("const T = struct { rows: [2]struct { a: u8 } };").Arg3.Content.ShouldBeOfType<Zig.InlineStructType>();
        FirstNode<Zig.StructFieldDefault>("const T = struct { link: if (c) void else [3]struct { v: u8 } = undefined };")
            .Arg2.Content.ShouldBeOfType<Zig.IfExpr>();
        TryParse("const T = struct { head: ?*const struct { n: u32 } = null };").ShouldBeTrue();
        TryParse("fn f() void { for ([_]struct { k: u8 }{ .{ .k = 1 } }) |e| { _ = e; } }").ShouldBeTrue();
        FirstNode<Zig.BuiltinCall>("fn f() void { _ = @as(struct { x: i32 }, .{ .x = 1 }); }").ShouldNotBeNull();
        // The named declaration keeps its own form.
        FirstNode<Zig.StructDecl>("const P = struct { x: u32 };").ShouldNotBeNull();
    }

    [Fact]
    public void An_if_in_a_type_position_is_an_if_type()
    {
        FirstNode<Zig.TySlice>("const T = struct { v: []if (c) u32 else u8 };").Arg2.Content.ShouldBeOfType<Zig.TyIf>();
        FirstNode<Zig.TyOptional>("const T = struct { m: ?if (c) *u8 else noreturn };").Arg1.Content.ShouldBeOfType<Zig.TyIf>();
        FirstNode<Zig.FnDef>("fn f(x: if (c) u64 else u32) if (c) u64 else u32 { return x; }").ShouldNotBeNull();
        FirstNode<Zig.EnumDeclTyped>("const E = enum(if (c) u16 else u8) { a, b };").Arg5.Content.ShouldBeOfType<Zig.TyIf>();
        // Where a value can stand, the same spelling is the value `if`.
        FirstNode<Zig.IfExpr>("fn f() void { const v = if (c) a else b; _ = v; }").ShouldNotBeNull();
    }

    [Fact]
    public void Inline_enums_and_unions_are_types_anywhere()
    {
        FirstNode<Zig.TyOptional>("const T = struct { m: ?enum { a, b } = null };").Arg1.Content.ShouldBeOfType<Zig.InlineEnumType>();
        FirstNode<Zig.TyArray>("const T = struct { f: [2]enum(u8) { off, on } };").Arg3.Content.ShouldBeOfType<Zig.InlineEnumTypeTyped>();
        TryParse("fn f(s: *const union(enum) { a: u32, b: u16 }) void { _ = s; }").ShouldBeTrue();
        // A type-choosing `if` arm and an argument reach the same records.
        FirstNode<Zig.IfExpr>("const E = if (c) enum { a } else enum(u8) { b };").Arg4.Content.ShouldBeOfType<Zig.InlineEnumType>();
        FirstNode<Zig.BuiltinCall>("fn f() void { _ = @as(enum { x, y }, .y); }").ShouldNotBeNull();
        // The named declarations keep their own forms.
        FirstNode<Zig.EnumDecl>("const E = enum { a, b };").ShouldNotBeNull();
        FirstNode<Zig.UnionDeclEnum>("const U = union(enum) { a: u8 };").ShouldNotBeNull();
    }

    [Fact]
    public void Range_multi_object_and_capture_while_loops_are_values()
    {
        FirstNode<Zig.ForMultiElseExpr>("fn f(a: []const u8) usize { const i = for (a, 0..) |x, j| { if (x > 0) break j; } else 0; return i; }")
            .Arg10.Content.ShouldBeOfType<Zig.IntLit>();
        FirstNode<Zig.ForRangeElseExpr>("fn f(n: usize) void { k -= for (0..n) |i| { if (i > 2) break i; } else 0; }").ShouldNotBeNull();
        FirstNode<Zig.WhileCaptureElseExpr>("fn f(it: ?*N) void { const n = while (it) |x| { break x; } else { return; }; _ = n; }")
            .Arg9.Content.ShouldBeOfType<Zig.Block>();
        // At statement start the statement loop keeps its `else`.
        FirstNode<Zig.StmtForMultiElse>("fn f(a: []const u8) void { for (a, 0..) |x, j| { _ = x; _ = j; } else {} }").ShouldNotBeNull();
    }

    [Fact]
    public void Small_std_shapes_3_parse()
    {
        FirstNode<Zig.PjBreakValue>("fn f(k: u8) u8 { return while (true) { switch (k) { 0 => break 1, else => {} } }; }").ShouldNotBeNull();
        FirstNode<Zig.FbBreakValue>("fn f(k: u8) u8 { return while (true) { switch (k) { else => if (k > 1) break 0 } }; }").ShouldNotBeNull();
        FirstNode<Zig.WhileNoElseExpr>("fn f() u8 { const x: u8 = while (true) { break 3; }; return x; }").ShouldNotBeNull();
        // An `else` after the Block belongs to the value `while`, not to an enclosing `if`.
        FirstNode<Zig.IfExprNoElse>("fn f(c: bool) void { _ = if (c) while (c) { break; } else 1; }")
            .Arg4.Content.ShouldBeOfType<Zig.WhileElseExpr>();
        FirstNode<Zig.StmtForSliceRefElse>("fn f(xs: []u8) !void { for (xs) |*x| { x.* = 0; } else return error.E; }").ShouldNotBeNull();
        FirstNode<Zig.MemberExternFn>("const H = struct { pub extern fn g(x: u32) void; };").ShouldNotBeNull();
    }

    [Fact]
    public void Small_std_shapes_4_parse()
    {
        FirstNode<Zig.ProngCaptureRefAssign>("fn f(e: *E) void { switch (e.*) { .len => |*l| l.* -= 1, else => {} } }").ShouldNotBeNull();
        FirstNode<Zig.ParamUnnamed>("extern fn g(*const u32, n: u32) bool;").ShouldNotBeNull();
        FirstNode<Zig.InlineForMultiElseExpr>("fn f() usize { return inline for (a, b,) |x, y| { _ = x; break y; } else 0; }").ShouldNotBeNull();
        // zig's `comptime Expr;`: the `;` closes it.
        FirstNode<Zig.ComptimeLoop>("fn f() void { comptime for (xs) |x| { _ = x; }; }").ShouldNotBeNull();
    }

    [Fact]
    public void Small_std_shapes_5_parse()
    {
        FirstNode<Zig.MemberExternVar>("const C = struct { extern threadlocal var errno: c_int; };").ShouldNotBeNull();
        FirstNode<Zig.ExternConst>("pub extern const etext: anyopaque;").ShouldNotBeNull();
        FirstNode<Zig.StmtWhileContAssignElse>("fn f(n: u32) ?u32 { var i: u32 = 0; while (i < n) : (i += 1) { break; } else return null; return i; }")
            .ShouldNotBeNull();
        FirstNode<Zig.ForElseExpr>("fn f(xs: []u8) u8 { return for (xs) |x| { break x; } else { return 0; }; }").Arg9.Content.ShouldBeOfType<Zig.Block>();
        FirstNode<Zig.FnTypeParamVariadic>("const F = *const fn (h: *u8, ...) callconv(.c) u32;").ShouldNotBeNull();
        FirstNode<Zig.FnTypeParamComptimeUnnamed>("const F = fn (comptime type, anytype) void;").ShouldNotBeNull();
    }

    [Fact]
    public void Small_std_shapes_6_parse()
    {
        FirstNode<Zig.PackedStructDecl>("const S2 = packed struct {};").ShouldNotBeNull();
        FirstNode<Zig.SwitchExprEmpty>("fn f() u8 { return g() catch |err| switch (err) {}; }").ShouldNotBeNull();
        FirstNode<Zig.StmtWhileCaptureContBlock>("fn f(it0: ?*N) void { var it = it0; while (it) |n| : ({ it = n.next; }) { _ = n; } }")
            .ShouldNotBeNull();
        FirstNode<Zig.MemberExternConst>("const P = struct { pub extern const etext: anyopaque; };").ShouldNotBeNull();
    }

    [Fact]
    public void Small_std_shapes_parse()
    {
        // `struct { … }{ … }`: a typed literal whose type is an inline struct; `const X = struct {…};` keeps its decl form.
        FirstNode<Zig.TypedStructInit>("fn f() u32 { const p = struct { x: u32 }{ .x = 3 }; return p.x; }")
            .Arg0.Content.ShouldBeOfType<Zig.InlineStructType>();
        TryParse("const P = struct { x: u32 };").ShouldBeTrue();
        FirstNode<Zig.InlineEnumTypeTyped>("fn f() void { g(enum(u8) { a, b }, 1); }").ShouldNotBeNull();
        FirstNode<Zig.StmtForRangeElse>("fn f(n: usize) void { for (0..n) |i| { _ = i; } else unreachable; }").ShouldNotBeNull();
        FirstNode<Zig.TyPointerAlign>("const P = *allowzero anyopaque;").Arg1.Content.ShouldBeOfType<Zig.PtrAllowzero>();
        FirstNode<Zig.TySentPtrConstAlign>("fn f(p: [*:0]align(1) const u16) void { _ = p; }").ShouldNotBeNull();
        // An expression then-arm on a statement `if` with an `else`: a ThenExpr, not an expression statement.
        FirstNode<Zig.StmtIfCaptureReturnErrElse>("fn f(x: E!u8) void { if (x) |s| g(s) else |_| {} }")
            .Arg7.Content.ShouldBeOfType<Zig.ThenExpr>();
    }

    [Fact]
    public void The_shapes_the_old_jump_copies_broke_still_parse_right()
    {
        // `x orelse return a == b & c` returns `a == (b & c)` (& binds tighter than ==); with the jump as a plain Bitwise
        // result the merged states once read it as `(a == b) & c`.
        var ret = FirstNode<Zig.OrElse>("fn f(x: ?bool, a: u8, b: u8, c: u8) bool { return x orelse return a == b & c; }")
            .Arg2.Content.ShouldBeOfType<Zig.ReturnExpr>();
        ret.Arg1.Content.ShouldBeOfType<Zig.CmpEq>().Arg2.Content.ShouldBeOfType<Zig.BitAnd>();
        // A jump fallback inside a call's argument (`f(a orelse continue)` once stopped parsing), and a top-level
        // `f() catch return` statement.
        TryParse("fn g(a: ?u8) void { while (true) { h(a orelse continue); } }").ShouldBeTrue();
        TryParse("fn k() !void { e() catch return; }").ShouldBeTrue();
    }

    [Fact]
    public void An_if_expression_follows_orelse_and_comparison_operators()
    {
        // `x orelse if (c) a else b orelse d`: the else arm is `b orelse d` (zig's greedy reading).
        var oe = FirstNode<Zig.OrElse>("fn f(x: ?u8, y: ?u8, c: bool) u8 { return x orelse if (c) 1 else y orelse 2; }");
        oe.Arg2.Content.ShouldBeOfType<Zig.IfExpr>().Arg6.Content.ShouldBeOfType<Zig.OrElse>();
        FirstNode<Zig.CmpEq>("fn g(a: u8, c: bool) bool { return a == if (c) 1 else 2; }").Arg2.Content.ShouldBeOfType<Zig.IfExpr>();
        // A nested `if` as an else arm, and `++` over an `if`.
        FirstNode<Zig.IfExpr>("fn h(a: bool, b: bool) u8 { return if (a) 1 else if (b) 2 else 3; }").Arg6.Content.ShouldBeOfType<Zig.IfExpr>();
        FirstNode<Zig.Concat>("const s = \"ab\" ++ if (true) \"c\" else \"d\";").Arg2.Content.ShouldBeOfType<Zig.IfExpr>();
    }
}

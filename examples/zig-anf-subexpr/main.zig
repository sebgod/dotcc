// dotcc Zig front-end — catch/orelse in SUB-expression positions (the ANF statement-hoist).
//
// `a catch b` (with a side-effecting fallback), `a catch return`, and `a orelse return` used to lower
// only as a full `const`/`var`/`return`/assignment RHS. dotcc now hoists them out of a SUB-expression
// too (`x + (a catch b())`, `f(a orelse return v)`): the value-producing construct lifts to a
// `__anf` temp before the enclosing statement, and the containing expression reads the temp — an
// A-normal-form transform. Eval order is preserved: a side effect to the LEFT of the construct makes
// the hoist a clear error (bind it to a `const` first) rather than a silent reorder.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-anf-subexpr/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const E = error{Bad};

fn mk(ok: bool) E!i32 {
    if (ok) return 5;
    return error.Bad;
}

fn dflt() i32 {
    return 27;
}

fn find(x: i32) ?i32 {
    if (x > 0) return x;
    return null;
}

// `orelse return` inside `100 + (…)` — on none, the whole function returns 7.
fn g(x: i32) i32 {
    return 100 + (find(x) orelse return 7);
}

pub fn main() u8 {
    // A side-effecting `catch` as a SUB-operand of `+` (decl-init position).
    const a: i32 = 2 + (mk(false) catch dflt()); // error → 2 + 27 = 29

    // The same, in an assignment (here `mk(true)` succeeds, so `dflt()` is not called).
    var b: i32 = 0;
    b = 1 + (mk(true) catch dflt()); // 1 + 5 = 6

    // `orelse return` hoisted out of a sub-expression (here `g` returns 7 on the none path).
    const c: i32 = g(-1); // 7

    _ = printf("a=%d b=%d c=%d\n", a, b, c);
    return @as(u8, @intCast(a + b + c)); // 29 + 6 + 7 = 42
}

// dotcc Zig front-end — destructuring completeness (Milestone S).
//
// Milestone G added the new-binding destructure `const a, const b = e;`. This completes the surface:
//   • assign-to-EXISTING lvalues — `a, b = .{ b, a };`
//   • MIXED — `const d, c = .{ 5, 6 };` (a fresh const + an existing var)
//   • TYPED binders — `const e: u16, const f: u8 = .{ 300, 7 };` (the binder type is the element's
//     result location, so a bare `300` lands as a u16)
//   • the `_` DISCARD binder — `_, g = .{ 99, 8 };`
//
// Zig destructuring is SEQUENTIAL, not snapshotting: for a tuple-LITERAL right-hand side the elements
// are bound/assigned in source order, and an existing-lvalue write is visible to a LATER element's
// read. So `a, b = .{ b, a }` is NOT a swap — `a` takes the old `b`, then `b` takes the NEW `a`. dotcc
// lowers a literal RHS element-wise with no temp (faithful to this), and a non-literal tuple RHS (a fn
// call) once into a temp then reads positionally.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-destructure/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

fn pair() struct { u8, u8 } {
    return .{ 20, 22 };
}

pub fn main() u8 {
    // Assign to existing lvalues, tuple-literal RHS — element-wise, source order (NOT a swap):
    // a <- old b (9), then b <- the NEW a (9).
    var a: u8 = 3;
    var b: u8 = 9;
    a, b = .{ b, a };
    if (a != 9 or b != 9) return 1;

    // 3-way: p <- old q (2), q <- old r (3), r <- the NEW p (2).
    var p: u8 = 1;
    var q: u8 = 2;
    var r: u8 = 3;
    p, q, r = .{ q, r, p };
    if (p != 2 or q != 3 or r != 2) return 2;

    // Mixed: a fresh `const d` + an existing `c`.
    var c: u8 = 0;
    const d, c = .{ 5, 6 };
    if (d != 5 or c != 6) return 3;

    // Typed binders drive each element's result location: `300` lands as a u16.
    const e: u16, const f: u8 = .{ 300, 7 };
    if (e != 300 or f != 7) return 4;

    // `_` discard binder.
    var g: u8 = 0;
    _, g = .{ 99, 8 };
    if (g != 8) return 5;

    // Non-literal tuple RHS: evaluated ONCE into a temp, then read positionally.
    const x, const y = pair();
    if (x != 20 or y != 22) return 6;

    _ = printf("a=%u b=%u p=%u q=%u r=%u d=%u c=%u e=%u f=%u g=%u x=%u y=%u\n", @as(c_uint, a), @as(c_uint, b), @as(c_uint, p), @as(c_uint, q), @as(c_uint, r), @as(c_uint, d), @as(c_uint, c), @as(c_uint, e), @as(c_uint, f), @as(c_uint, g), @as(c_uint, x), @as(c_uint, y));

    return c + f + x + g + 1; // 6 + 7 + 20 + 8 + 1 = 42
}

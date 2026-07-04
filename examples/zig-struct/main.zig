// dotcc Zig front-end — structs & enums (Milestone D1): a `struct` container with a
// result-located `.{…}` literal + field access (incl. through a pointer), and an
// `enum(T)` with dotted member access, `@intFromEnum`, and a `switch` over it.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-struct/main.zig --emit=file -o out.cs
//             dotnet run out.cs                          # -> "x=40 y=2 sum=42 color=2 rank=42", exit 42
//   real zig: zig build-exe main.zig -lc && ./main       # -> same
extern fn printf(format: [*c]const u8, ...) c_int;

// A struct container bound to a `const` (Zig: types are values). Lowers to a real C#
// `unsafe struct Point` via the SAME aggregate machinery the C frontend uses. The `pub`
// visibility modifier is peeled (Unwrap) — a no-op in dotcc's single-module emit.
pub const Point = struct { x: u8, y: u8 };

// A `pub` enum with an explicit underlying type → C# `enum Color : byte` (members auto-increment).
pub const Color = enum(u8) { red, green, blue };

// A `*Point` parameter — Zig has no `->`, so `p.x` auto-derefs (dotcc emits C#'s `->`).
fn sumPoint(p: *Point) u8 {
    return p.x + p.y;
}

// `switch` over an enum with dotted `.member` cases + `else` (exhaustive). The subject
// and the case labels decay to the underlying integer (shared enum-switch lowering).
fn rank(c: Color) u8 {
    switch (c) {
        .red => { return 1; },
        .green => { return 42; },
        else => { return 9; },
    }
}

pub fn main() u8 {
    // A result-located anonymous struct literal — the struct type comes from the
    // annotation, so `.{ .x = …, .y = … }` lowers to `new Point { x = …, y = … }`.
    var p: Point = .{ .x = 40, .y = 2 };
    const sum = sumPoint(&p);

    // A sink-typed enum literal + the enum→int decay.
    const c: Color = .blue;
    const colorVal = @intFromEnum(c); // blue = 2

    const r = rank(.green); // 42

    _ = printf("x=%d y=%d sum=%d color=%d rank=%d\n", @as(c_int, p.x), @as(c_int, p.y), @as(c_int, sum), @as(c_int, colorVal), @as(c_int, r));
    return sum; // 42
}

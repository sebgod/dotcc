// dotcc Zig front-end — TYPED struct literal `Point{ … }` (Zig's CurlySuffixExpr:
// `TypeExpr InitList?`). Unlike the anonymous, result-located `.{ … }` form, the type is
// named, so the literal carries its own type and needs NO sink — it is valid even in a
// sink-less position such as an immediate field access `(Point{ … }).y`, which `.{ … }`
// cannot express. Both forms lower to the same C# object initializer.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-struct-typed/main.zig --emit=file -o out.cs
//             dotnet run out.cs                       # -> exit 42
//   real zig: zig build-exe main.zig && ./main         # -> same
const Point = struct { x: u8, y: u8 };

pub fn main() u8 {
    // Typed literal in a value position → C# `new Point { x = 40, y = 2 }`.
    const p = Point{ .x = 40, .y = 2 };
    // Typed literal in a SINK-LESS position — immediate field access on the literal.
    const j = (Point{ .x = 5, .y = 9 }).y; // 9
    return p.x + p.y - 9 + j;              // 40 + 2 - 9 + 9 = 42
}

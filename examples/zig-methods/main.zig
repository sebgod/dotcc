// dotcc Zig front-end — struct METHODS & UFCS (Milestone D2). A struct body holds methods
// alongside its fields. Each method lowers to a mangled free function `Point_<name>` with the
// receiver as its ordinary first parameter, so `self.x` is plain field access. Three call
// forms, all rewritten to that free function:
//   * `Point.init(20, 1)` — a STATIC / associated function (first param is not a receiver):
//                           called on the type, every argument explicit, no receiver.
//   * `p.scale(2)`         — an INSTANCE method with a `*Point` receiver: UFCS auto-takes the
//                           address (`&p`) and `self.x` auto-derefs to C#'s `self->x`.
//   * `p.sum()`            — an INSTANCE method whose receiver type is `@This()` (the enclosing
//                           container), passed by value.
// `const Self = @This();` (a container-level const alias) and enum methods are not lowered yet.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-methods/main.zig --emit=file -o out.cs
//             dotnet run out.cs                       # -> exit 42
//   real zig: zig build-exe main.zig && ./main         # -> same
const Point = struct {
    x: u8,
    y: u8,

    // Associated function (no receiver) — called as `Point.init(…)`.
    fn init(x: u8, y: u8) Point {
        return .{ .x = x, .y = y };
    }

    // Pointer receiver — mutates the instance in place (`&p` auto-ref at the call site).
    fn scale(self: *Point, f: u8) void {
        self.x = self.x * f;
        self.y = self.y * f;
    }

    // Value receiver named via `@This()` — the enclosing container type.
    fn sum(self: @This()) u8 {
        return self.x + self.y;
    }
};

pub fn main() u8 {
    var p = Point.init(20, 1); // {20, 1}
    p.scale(2);                // {40, 2}
    return p.sum();            // 40 + 2 = 42
}

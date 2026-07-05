// In-function container declarations (wall-plan W2) — a `const P = struct { … };`
// declared inside a function body, not just at the top level. dotcc registers a
// local struct into the module type section on the fly during body lowering, under
// a function-mangled name (`<fn>__<P>`), so two functions may declare a same-named
// but differently-shaped local without colliding, and a local never leaks over a
// top-level type of the same name. V1 is struct-only, fields-only (a local
// enum/union, or a method/const member in a local struct, is a loud error).
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-in-fn-struct/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

fn area() i32 {
    const Rect = struct { w: i32, h: i32 };
    const r: Rect = .{ .w = 6, .h = 7 };
    return r.w * r.h;
}

fn sum() i32 {
    // same local name, a different shape — a distinct type from area()'s Rect.
    const Rect = struct { a: i32, b: i32, c: i32 };
    const r: Rect = .{ .a = 10, .b = 20, .c = 30 };
    return r.a + r.b + r.c;
}

pub fn main() void {
    const Point = struct { x: i32, y: i32 };
    const p: Point = .{ .x = 3, .y = 4 };
    _ = printf("p=%d,%d area=%d sum=%d\n", p.x, p.y, area(), sum());
}

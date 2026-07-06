// `anytype` parameters (wall-plan W5) — the capstone on the monomorphization spine.
// An `a: anytype` parameter has NO written type: its type is INFERRED from the actual argument
// (`T := @TypeOf(arg)`), and — like a `comptime T: type` parameter — that inferred type keys a fresh
// specialization (`add__i32_i32`, `add__f64_f64`). The difference from a comptime TYPE parameter: the
// argument is a VALUE passed at runtime (not a type spelling consumed at compile time), so an anytype
// parameter is a HYBRID — a monomorphization key AND a runtime slot. The body is duck-typed: a use that
// the inferred type supports (arithmetic, a `.field`, a `.len`) lowers against the concrete type; a
// mismatch fails PER INSTANTIATION, exactly as in real Zig (and C++ templates).
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-anytype/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

const Point = struct { x: i32, y: i32 };

// The `@TypeOf(a)` return type resolves to the FIRST parameter's inferred type — so `add` of two i32s
// returns i32, and `add` of two f64s returns f64, with no separate declaration.
fn add(a: anytype, b: anytype) @TypeOf(a) {
    return a + b;
}

fn maxOf(a: anytype, b: anytype) @TypeOf(a) {
    return if (a > b) a else b;
}

// Duck-typed member access: `p.x` requires only that the inferred type HAS an `x` field.
fn getX(p: anytype) i32 {
    return p.x;
}

// Duck-typed `.len`: works for any inferred type carrying a length (here, a slice).
fn firstLen(s: anytype) usize {
    return s.len;
}

pub fn main() void {
    _ = printf("add_i=%d add_f=%.1f\n", add(@as(i32, 3), @as(i32, 4)), add(@as(f64, 1.5), @as(f64, 2.5)));
    _ = printf("max_i=%d max_f=%.1f\n", maxOf(@as(i32, 7), @as(i32, 2)), maxOf(@as(f64, 1.5), @as(f64, 4.5)));
    const p = Point{ .x = 42, .y = 7 };
    const arr = [_]i32{ 1, 2, 3, 4 };
    _ = printf("x=%d len=%d\n", getX(p), @as(i32, @intCast(firstLen(arr[0..]))));
}

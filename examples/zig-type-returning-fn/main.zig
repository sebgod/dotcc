// Type-returning functions (wall-plan W4) — the ArrayList shape, the last generative brick.
// A `fn Name(comptime T: type) type { return struct { … }; }` is a COMPTIME type constructor: it
// emits no runtime code; each use in a TYPE position REIFIES a fresh struct per resolved type
// argument (`Pair__i32`, `Pair__f64`, `Node__i32`), memoized so the same argument reuses one struct.
// Fields typed `T` become the concrete type; `?*@This()` becomes a self-pointer.
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-type-returning-fn/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

// Pair(i32) → `struct Pair__i32 { int a; int b; }`; Pair(f64) → a distinct `Pair__f64`.
fn Pair(comptime T: type) type {
    return struct { a: T, b: T };
}

// A self-referential type: `next` is a pointer to the reified type itself (via @This()).
fn Node(comptime T: type) type {
    return struct { value: T, next: ?*const @This() };
}

// A top-level alias for a reified type — `Pair(i32)` keyed by the resolved type.
const PairI32 = Pair(i32);

pub fn main() void {
    const pi: PairI32 = .{ .a = 3, .b = 4 };
    const pf: Pair(f64) = .{ .a = 1.5, .b = 2.5 };
    const tail: Node(i32) = .{ .value = 20, .next = null };
    const head: Node(i32) = .{ .value = 10, .next = &tail };
    _ = printf("pi=%d,%d pf=%.1f,%.1f\n", pi.a, pi.b, pf.a, pf.b);
    _ = printf("head=%d tail=%d\n", head.value, head.next.?.value);
}

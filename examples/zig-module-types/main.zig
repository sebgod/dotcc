// TYPES across the `@import` seam — road-to-zig-std S4d.
//
// dotcc has resolved imported FUNCTIONS since the module graph landed (S1/S2). Types were the gap:
// a dotted type went to the curated std-type registry and, missing there, failed — so no type from
// another module (real `std` included) could be named at all. Now a dotted type that isn't curated
// falls back to the module graph, which is what lets `std.ArrayList` reach real std source.
//
// Everything below crosses a module boundary:
//   - `geom.Point`      a struct type from a sibling module, used in an annotation
//   - `p.get(.x)`       a METHOD on it — declared on demand at this call site, lowered in ITS module
//   - `geom.Axis`       an enum from a sibling module, with a bare `.member` literal
//   - `list.Box(i32)`   a type-returning GENERIC from a sibling module, reified per type argument
//
// The curated std model still wins wherever it claims a path (`std.mem.Allocator` and friends are
// dotcc's own runtime types, never navigated) — navigation is the fallback, not the front door.
//
//   dotnet run --project DotCC -c Release -- --emit=file examples/zig-module-types/main.zig > out.cs
//   dotnet out.cs   # prints the four lines below, exits 42
//
// Output:
//   point   x=40 y=2 sum=42
//   axis    get(.x)=40 get(.y)=2
//   box i32 first=40 len=3 total=43
//   box u8  first=2 len=1 total=3

const geom = @import("geom.zig");
const list = @import("list.zig");

extern fn printf(fmt: [*:0]const u8, ...) c_int;

// An instantiation bound to an alias, exactly as `const List = std.ArrayList(u8);` is written.
const ByteBox = list.Box(u8);

pub fn main() u8 {
    const p: geom.Point = .{ .x = 40, .y = 2 };
    _ = printf("point   x=%d y=%d sum=%d\n", p.x, p.y, p.sum());

    // A bare `.member` literal resolves against the imported enum the sink names.
    const ax: geom.Axis = .x;
    _ = printf("axis    get(.x)=%d get(.y)=%d\n", p.get(ax), p.get(.y));

    // The generic instantiated straight off the module-qualified call — no alias needed.
    var b: list.Box(i32) = list.Box(i32).init(40);
    b.bump(2);
    _ = printf("box i32 first=%d len=%llu total=%llu\n", b.first, b.len, b.total());

    // …and through an alias, at a different type argument: two independent reified structs.
    const sb = ByteBox.init(2);
    _ = printf("box u8  first=%d len=%llu total=%llu\n", sb.first, sb.len, sb.total());

    return @intCast(p.sum());
}

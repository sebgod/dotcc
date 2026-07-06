// std.debug.print (wall-plan W6) — the biggest remaining std idiom, and the last brick of the
// comptime/generics arc. `std.debug.print("{d} {s}\n", .{n, s})` writes a formatted line to STDERR
// (exactly as real Zig does — std.debug.print is stderr, not stdout).
//
// No comptime reflection is needed: the format string is a comptime literal, so dotcc parses it AT
// LOWERING TIME and pairs its `{…}` placeholders POSITIONALLY with the argument tuple's elements,
// translating each to the equivalent C printf conversion and lowering to
// `fprintf(stderr, "<C-fmt>").Arg(…).Done()` over dotcc's existing printf-builder. Because the runtime
// builder keys off the actual argument type, `{d}` prints any integer width correctly.
//
// Curated subset: {}, {d}, {s}, {c}, {x}, {X}; `{{`/`}}` escape to literal braces. A float / slice
// argument, a width/named specifier ({d:0>5}), or {any} is a clear, specific error.
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-debug-print/main.zig
//        (the output is on stderr — redirect `2>&1` to see it)
// Zig:   zig run main.zig     (same stderr output — the differential oracle's claim)
const std = @import("std");

const Point = struct { x: i32, y: i32 };

pub fn main() void {
    const n: i32 = 42;
    const big: i64 = 5000000000; // {d} prints the full 64-bit value (no length modifier needed)
    const p = Point{ .x = 3, .y = 7 };
    std.debug.print("hello {s}! n={d} big={d}\n", .{ "world", n, big });
    std.debug.print("hex={x} up={X} char={c}\n", .{ 255, 255, 65 });
    std.debug.print("point {{x={d}, y={d}}} pct=100%\n", .{ p.x, p.y });
}

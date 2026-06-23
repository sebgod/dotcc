// dotcc Zig front-end — `std.heap.ArenaAllocator` (Milestone U).
//
// An arena bump-allocates from a backing allocator in growing chunks and reclaims EVERYTHING at
// once in `deinit()` — the headline pairing with `defer arena.deinit()`. dotcc models it as the
// runtime `ArenaAllocator { backing, chunk-chain }` (in `DotCC.Libc/ZigAlloc.cs`): `init(backing)`
// wraps the backing allocator (here the statically-known default, which materializes a runtime
// C-heap `Allocator`), `allocator()` hands out an opaque `Allocator` whose `.alloc` bumps the arena
// (the INDIRECT vtable path), and `deinit()` frees the whole chunk chain back to the backing.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-arena/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "total=42")
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

extern fn printf(format: [*c]const u8, ...) c_int;

fn run() !u8 {
    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit(); // frees every chunk at scope exit — no per-allocation free needed
    const a = arena.allocator();

    const s1 = try a.alloc(u8, 3);
    s1[0] = 10;
    s1[1] = 11;
    s1[2] = 9;

    const s2 = try a.alloc(u8, 2);
    s2[0] = 7;
    s2[1] = 5;

    const total = s1[0] + s1[1] + s1[2] + s2[0] + s2[1]; // 10+11+9+7+5 = 42
    _ = printf("total=%d\n", @as(c_int, total));
    return total;
}

pub fn main() u8 {
    return run() catch 1;
}

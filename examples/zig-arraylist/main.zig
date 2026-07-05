// Curated std.ArrayList(T) (wall-plan W0) — the modern UNMANAGED array list
// (zig 0.15+): `.empty`, a per-call allocator, `pop()` → ?T. dotcc lowers the
// type to the runtime ZigList<T> and the curated member set to its instance
// methods; `capacity` is supported but its VALUE is the growth policy's detail
// (dotcc doubles; real zig's curve differs), so this example doesn't print it.
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-arraylist/main.zig
// Zig:   zig build-exe main.zig -lc   (same output — the differential oracle's claim)
const std = @import("std");
extern fn printf(fmt: [*:0]const u8, ...) c_int;

pub fn main() !void {
    const alloc = std.heap.c_allocator;
    var list: std.ArrayList(i32) = .empty;
    defer list.deinit(alloc);

    var i: i32 = 0;
    while (i < 10) : (i = i + 1) {
        try list.append(alloc, i * 3);
    }
    _ = printf("len=%zu first=%d last=%d\n", list.items.len, list.items[0], list.items[9]);

    const popped = list.pop();
    if (popped) |v| {
        _ = printf("popped=%d len=%zu\n", v, list.items.len);
    }

    var sum: i32 = 0;
    for (list.items) |x| {
        sum = sum + x;
    }

    const tail = [2]i32{ 100, 200 };
    try list.appendSlice(alloc, &tail);
    _ = printf("sum=%d after=%zu back=%d\n", sum, list.items.len, list.items[10]);
}

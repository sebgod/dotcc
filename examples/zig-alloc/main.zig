// dotcc Zig front-end — allocators (Milestone F).
//
// Zig's `std.mem.Allocator` is a fat pointer `{ ptr, *vtable }`; `a.alloc(T, n)` /
// `a.free(s)` dispatch through the vtable. dotcc models the abstraction AND
// devirtualizes the statically-known default:
//
//   * `std.heap.page_allocator` (a comptime const) is the C-heap default — `a.alloc` /
//     `a.free` on it lower to a DIRECT `Libc.malloc` / `free` (no vtable).
//   * a `std.heap.FixedBufferAllocator` is a second, runtime-selected allocator —
//     `fba.allocator()` hands out an `Allocator` whose `.alloc` dispatches INDIRECTLY
//     through its vtable.
//   * an opaque `std.mem.Allocator` PARAMETER is always indirect; the default passed to
//     one materializes a runtime C-heap `Allocator` value.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-alloc/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

// Takes the allocator abstractly — dotcc can't prove which concrete allocator this is,
// so `a.alloc` dispatches through the vtable (the indirect path).
fn fill(a: std.mem.Allocator, n: usize, v: u8) ![]u8 {
    const s = try a.alloc(u8, n);
    var i: usize = 0;
    while (i < n) : (i = i + 1) {
        s[i] = v;
    }
    return s;
}

fn run() !u8 {
    // The default allocator — devirtualized to a direct malloc/free at its own call sites,
    // and materialized into a runtime Allocator when passed to `fill` below.
    const heap = std.heap.page_allocator;
    const a = try heap.alloc(u8, 3);
    a[0] = 4;
    a[1] = 8;
    a[2] = 6;

    // A fixed buffer on the stack, handed out through a real vtable (the indirect path).
    var buffer: [64]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buffer);
    const b = try fill(fba.allocator(), 3, 5); // 3 * 5 = 15

    // The default, passed abstractly to `fill` (materialized + indirect): 3 * 3 = 9.
    const c = try fill(heap, 3, 3);

    const total = a[0] + a[1] + a[2] + b[0] + b[1] + b[2] + c[0] + c[1] + c[2]; // 18 + 15 + 9 = 42
    heap.free(a);
    return total;
}

pub fn main() u8 {
    return run() catch 1;
}

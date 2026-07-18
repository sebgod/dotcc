// dotcc Zig front-end — allocator resize / remap (a FixedBufferAllocator).
//
// `a.resize(slice, n)` changes a block's length IN PLACE and returns whether it
// succeeded (Zig's `bool`); `a.remap(slice, n)` is resize-possibly-moving and
// returns the (re)sized slice or null (`?[]T`). A FixedBufferAllocator answers
// both DETERMINISTICALLY from its bump cursor — the LAST allocation can grow
// (if the buffer has room) or shrink, an earlier block can only shrink in place —
// so dotcc wires them for a provable `fba.allocator()` site (a direct
// `ZigAlloc.ResizeFba` / `RemapFba`, no vtable). On the C-heap default or an
// opaque `std.mem.Allocator` they stay a clear deferred error: that in-place
// result is page-dependent (real zig answers from malloc_usable_size), so use
// `realloc` there.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-resize-remap/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

fn run() !u8 {
    var buffer: [64]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buffer);
    const a = fba.allocator();

    var s = try a.alloc(u8, 4);
    var i: usize = 0;
    while (i < 4) : (i += 1) {
        s[i] = 3;
    }

    // Grow the last allocation to 6 via remap — an FBA never moves, so the pointer
    // is unchanged and the first four bytes are preserved.
    s = a.remap(s, 6) orelse return 2;
    if (s.len != 6) return 3;
    s[4] = 15;
    s[5] = 15;

    var sum: u8 = 0;
    var j: usize = 0;
    while (j < s.len) : (j += 1) {
        sum += s[j]; // 3*4 + 15 + 15 = 42
    }

    // Shrink the last allocation in place (rewinds the bump cursor) — returns true.
    if (!a.resize(s, 3)) return 4;

    return sum;
}

pub fn main() u8 {
    return run() catch 1;
}

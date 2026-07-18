// dotcc Zig front-end — resize / remap through an OPAQUE allocator (indirect dispatch).
//
// When the allocator arrives as a `std.mem.Allocator` PARAMETER, dotcc can't prove which
// concrete allocator it is, so `alloc`/`remap`/`resize` dispatch through the vtable
// (`Allocator.Alloc/Remap/Resize<T>`) instead of devirtualizing. The answer is whatever the
// runtime allocator gives — here it's a FixedBufferAllocator passed from `main`, so the in-place
// result is deterministic and byte-for-byte identical to real zig. (Only the statically-known
// C-heap default stays a deferred error for resize/remap: its in-place result is page-dependent —
// use `realloc` there.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-opaque-allocator/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

fn run(a: std.mem.Allocator) !u8 {
    var s = try a.alloc(u8, 4);
    var i: usize = 0;
    while (i < 4) : (i += 1) {
        s[i] = 3;
    }

    // Grow to 6 via remap — remap returns a fresh `?[]u8` (length 6), so we reassign the whole
    // slice; an FBA never moves, so the pointer is unchanged and the first four bytes are kept.
    s = a.remap(s, 6) orelse return 2;
    if (s.len != 6) return 3;
    s[4] = 15;
    s[5] = 15;

    var sum: u8 = 0;
    var j: usize = 0;
    while (j < s.len) : (j += 1) {
        sum += s[j]; // 3*4 + 15 + 15 = 42
    }

    // Shrink the last allocation in place via resize (checked for its bool result).
    if (!a.resize(s, 3)) return 4;

    return sum;
}

pub fn main() u8 {
    var buffer: [64]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buffer);
    // Pass `fba.allocator()` into a function typed `std.mem.Allocator` — opaque at the call sites.
    return run(fba.allocator()) catch 1;
}

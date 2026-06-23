// dotcc Zig front-end — `allocator.realloc` (Milestone U).
//
// `a.realloc(slice, n)` grows or shrinks a slice (Zig's `Error![]T`), preserving contents up to the
// smaller of old/new. dotcc reuses the alloc devirt fork: the statically-known C-heap default →
// a direct `Libc.realloc` (`ZigAlloc.ReallocCHeap`, no vtable); a devirtualized FBA site → an
// emulated bump+copy (`ZigAlloc.ReallocFba`); an opaque allocator → an emulated alloc+copy+free
// through its 2-fn vtable (`recv.Realloc`). The sibling `resize`/`remap` (in-place / optional) are
// deferred — their result is allocator-page-dependent — so use `realloc`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-realloc/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "total=42")
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

extern fn printf(format: [*c]const u8, ...) c_int;

fn run() !u8 {
    const a = std.heap.page_allocator;

    var s = try a.alloc(u8, 2); // start small
    s[0] = 10;
    s[1] = 11;

    s = try a.realloc(s, 4); // grow; s[0], s[1] preserved
    s[2] = 12;
    s[3] = 9;

    const total = s[0] + s[1] + s[2] + s[3]; // 10+11+12+9 = 42
    _ = printf("total=%d\n", @as(c_int, total));
    a.free(s);
    return total;
}

pub fn main() u8 {
    return run() catch 1;
}

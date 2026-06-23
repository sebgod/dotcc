// dotcc — C↔Zig shared-heap interop (Milestone V): std.heap.c_allocator IS the C
// malloc/free heap, so memory allocated by one front-end is read / freed / resized
// by the other in one mixed program.
//
// Both translation units lower into one program (the C group builds the IR, the Zig
// group lowers in), and `std.heap.c_allocator` devirtualizes to a direct Libc.malloc
// — the SAME heap C's malloc/free use. So a Zig-allocated buffer can be freed by C,
// a C-allocated buffer read by Zig, and a Zig fn taking an opaque std.mem.Allocator
// works when its result crosses to C.
//
// NB: only c_allocator is cross-seam-safe — page_allocator is mmap/VirtualAlloc, a
// different heap from C's malloc.
//
//   dotcc:  dotnet run --project DotCC -c Release -- \
//             examples/zig-c-heap/main.zig examples/zig-c-heap/heap.c --emit=file -o out.cs
//           dotnet run out.cs                  # prints the sums; exits 42
const std = @import("std");

extern fn printf(format: [*c]const u8, ...) c_int;
extern fn make_ints(n: c_int) [*c]c_int;
extern fn sum_ints(p: [*c]c_int, n: c_int) c_int;
extern fn take_and_free(p: [*c]c_int) void;

// Takes an opaque std.mem.Allocator (indirect dispatch); the caller picks the heap.
fn build(a: std.mem.Allocator, n: usize) ![]c_int {
    const s = try a.alloc(c_int, n);
    var i: usize = 0;
    while (i < n) : (i = i + 1) {
        s[i] = @intCast(i + 1);
    }
    return s;
}

pub fn main() u8 {
    const a = std.heap.c_allocator;

    // (1) Zig allocates through c_allocator; C reads it (1+2+..+8 = 36), then C frees it.
    const s = build(a, 8) catch return 1;
    const built = sum_ints(s.ptr, 8);
    take_and_free(s.ptr);

    // (2) C mallocs a buffer; Zig reads it (1+2+3 = 6); C frees it.
    const raw = make_ints(3);
    var read: c_int = 0;
    var i: usize = 0;
    while (i < 3) : (i = i + 1) {
        read = read + raw[i];
    }
    take_and_free(raw);

    _ = printf("built=%d read=%d -> %d\n", built, read, built + read);
    return @intCast(built + read); // 36 + 6 = 42
}

// dotcc Zig front-end — non-escaping stack-slice peephole (Milestone O, part 5).
//
// A heap slice allocated through the DEVIRTUALIZED C-heap default allocator
// (`std.heap.page_allocator` — whose `.alloc` lowers to a direct `Libc.malloc`) is demoted to a
// `stackalloc` backing when escape analysis proves it never leaves the stack frame. This is the
// Zig analogue of dotcc's C `malloc`→`stackalloc` promotion, and the zero-heap win the slices
// design parked (a `[N]T` array local is already stack-backed; only an allocator-backed slice has
// a real heap allocation to eliminate).
//
// Here `buf` is allocated, filled + summed via `buf[i]` / `buf.len`, then freed — never returned,
// stored, or passed on — so dotcc emits `stackalloc byte[N]` instead of malloc/free. Real zig
// allocates on the heap and frees; both observe the same result (exit 42).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-stack-slice/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

fn run() !u8 {
    const a = std.heap.page_allocator; // the C-heap default — devirtualized at its alloc/free sites
    const buf = try a.alloc(u8, 6); // non-escaping, constant size, byte element → promotable

    var i: usize = 0;
    while (i < buf.len) : (i = i + 1) {
        buf[i] = 7; // 6 * 7 = 42
    }

    var sum: u32 = 0;
    i = 0;
    while (i < buf.len) : (i = i + 1) {
        sum = sum + buf[i];
    }

    a.free(buf); // dropped on promotion (the backing is stack memory)
    return @as(u8, @intCast(sum)); // 42
}

pub fn main() u8 {
    return run() catch 1;
}

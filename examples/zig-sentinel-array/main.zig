// dotcc Zig front-end — sentinel-terminated arrays `[N:0]T` (Milestone O, part 4).
//
// `[N:0]T` reserves N+1 elements of storage: the trailing slot (index N) holds the sentinel 0,
// while the logical length stays N. So a `[N:0]u8` literal of N bytes is a valid NUL-terminated
// C string with no hand-written terminator. dotcc lowers it to the same `stackalloc` an `[N]T`
// local uses, grown by one zeroed slot — the symbol's type is the N-element array, so indexing
// and slicing behave like `[N]T`; the extra slot is the sentinel, readable at `buf[N]`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-sentinel-array/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    // 5 logical bytes summing to 42; index 5 is the reserved sentinel slot (must read back 0).
    const buf: [5:0]u8 = .{ 10, 11, 12, 8, 1 };

    var sum: u32 = 0;
    var i: usize = 0;
    while (i < 5) : (i = i + 1) {
        sum = sum + buf[i];
    }
    // The N+1th slot is the sentinel — guaranteed 0, so the buffer is a NUL-terminated C string.
    if (buf[5] != 0) {
        sum = 0; // sentinel violated → force a wrong (non-42) exit
    }

    _ = printf("sum=%u sentinel=%u\n", sum, @as(c_uint, buf[5]));
    return @as(u8, @intCast(sum)); // 42
}

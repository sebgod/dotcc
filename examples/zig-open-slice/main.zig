// dotcc Zig front-end — open-ended slicing `s[lo..]` (Milestone O, part 1).
//
// `a[lo..]` slices from `lo` to the END of the source: the high bound is the source
// LENGTH (a slice's `.len`, an array's element count), so the result is the fat pointer
// `{ a.ptr + lo, sourceLen - lo }`. dotcc shares the `[lo..hi]` machinery; only the high
// bound differs. A bare pointer has no length, so open-ending one is rejected (as Zig does).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-open-slice/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    // open-ended slicing of a slice: the high bound is `.len`.
    const s: []const u8 = "abcdefghijklmnopqrstu"; // len 21
    const whole = s[0..]; // len 21
    const mid = s[7..]; // len 14
    const tail = s[14..]; // len 7

    // open-ended slicing of an array: the high bound is the element count.
    const arr = [_]u8{ 1, 2, 3, 4 };
    const at = arr[1..]; // len 3
    if (at.len != 3) return 1;

    // a closed re-slice still works alongside the open form.
    const cls = s[2..5]; // len 3
    if (cls.len != 3) return 1;

    const total = whole.len + mid.len + tail.len; // 21 + 14 + 7 = 42
    _ = printf("total=%zu at=%zu\n", total, at.len);
    return @as(u8, @intCast(total)); // 42
}

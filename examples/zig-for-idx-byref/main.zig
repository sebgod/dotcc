// dotcc Zig front-end — `for (s, 0..) |*e, i|`: by-reference element capture WITH the index (Z).
//
// Milestone M added by-reference element capture `for (s) |*e|` (mutate through `e.*`) and the
// indexed `for (s, 0..) |e, i|` separately. Milestone Z combines them: `for (s, 0..) |*e, i|` binds
// `e` as a `*T` INTO the slice (so `e.* = …` writes through) AND `i` as the usize index. dotcc reuses
// the for-over-slice lowering with both the by-ref element pointer and the index counter.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-for-idx-byref/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

// Add each element's index to it, in place, through the by-reference capture.
fn scaleByIndex(xs: []i32) void {
    for (xs, 0..) |*e, i| {
        e.* = e.* + @as(i32, @intCast(i));
    }
}

pub fn main() u8 {
    var arr = [_]i32{ 10, 10, 10, 10 };
    scaleByIndex(arr[0..]); // {10, 10, 10, 10} -> {10, 11, 12, 13}
    var total: i32 = 0;
    for (arr[0..]) |x| {
        total = total + x; // 10 + 11 + 12 + 13 = 46
    }
    return @intCast(total - 4); // 46 - 4 = 42
}

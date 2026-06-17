// dotcc Zig front-end — slices (Milestone E).
//
// `[]const u8` slice params, `.len` / `.ptr`, element index `s[i]`, the array→slice
// coercion (a string literal `*const [N:0]u8` → `[]const u8`), the slicing operator
// `s[lo..hi]` → a sub-slice, and for-over-slice (`for (s) |b|`).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-slices/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?
fn countL(s: []const u8) usize {
    var n: usize = 0;
    for (s) |b| {
        if (b == 108) { // 'l'
            n = n + 1;
        }
    }
    return n;
}

pub fn main() u8 {
    const s: []const u8 = "hello";
    const mid = s[1..4]; // "ell"
    if (s.len == 5) {
        if (countL(mid) == 2) { // "ell" has two 'l'
            return 42;
        }
    }
    return 0;
}

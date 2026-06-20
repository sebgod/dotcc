// dotcc Zig front-end — switch ranges (Milestone L, part 4).
//
// A switch prong value may be an inclusive range `lo...hi` (Zig uses `...`, distinct from the
// exclusive `..` of slices/for-ranges). dotcc lowers it to a C# relational pattern — `case >= lo
// and <= hi:` in a statement switch, `>= lo and <= hi => …` in a switch expression — which works
// because Zig requires comptime-known range bounds, exactly C#'s relational-pattern constraint.
// Ranges mix freely with single / multi-value prongs and `else`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-switch-range/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A switch EXPRESSION with ranges — classify a byte as digit / upper / lower / other.
fn kind(c: u8) u8 {
    return switch (c) {
        '0'...'9' => 1,
        'A'...'Z' => 2,
        'a'...'z' => 3,
        else => 0,
    };
}

pub fn main() u8 {
    // A STATEMENT switch with ranges plus a multi-value prong.
    var bucket: i32 = 0;
    const n: i32 = 42;
    switch (n) {
        0...9 => {
            bucket = 1;
        },
        10...99 => {
            bucket = 2;
        },
        100, 200, 300 => {
            bucket = 3;
        },
        else => {
            bucket = 9;
        },
    }
    // n=42 falls in 10...99 → bucket = 2.

    const d = kind('7'); // 1 (digit)
    const u = kind('Q'); // 2 (upper)
    const l = kind('z'); // 3 (lower)
    const o = kind('!'); // 0 (other)

    _ = printf("bucket=%d d=%d u=%d l=%d o=%d\n", bucket, d, u, l, o); // 2 1 2 3 0

    // bucket*18 (36) + d+u+l+o (6) = 42.
    return @as(u8, @intCast(bucket * 18 + d + u + l + o));
}

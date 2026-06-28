// dotcc Zig front-end — MULTI-variant tagged-union capture prong (Milestone Z).
//
// Milestone D3 captured a tagged-union payload only on a SINGLE-variant prong (`.circle => |r|`).
// Zig also allows listing several variants in one capture prong — `.circle, .square => |r|` — as long
// as every listed variant carries the SAME payload type, so the one capture `|r|` is well-typed. In
// dotcc's faithful tagged-union layout the payload is an explicit-layout union with every variant at
// `[FieldOffset(0)]`, so the variants overlap: dotcc binds `r` to the FIRST variant's field, which
// aliases whichever variant actually matched (same type, same offset). A multi-variant prong whose
// variants differ in payload type is a clear error.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-union-multi-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

const Shape = union(enum) {
    circle: i32,
    square: i32,
    name: u8,
};

fn area(s: Shape) i32 {
    switch (s) {
        // `.circle` and `.square` both carry i32 → one capture `|r|` binds the shared payload.
        .circle, .square => |r| {
            return r * 2;
        },
        .name => |c| {
            return @as(i32, c);
        },
    }
}

pub fn main() u8 {
    const a = Shape{ .circle = 9 };
    const b = Shape{ .square = 12 };
    return @intCast(area(a) + area(b)); // 18 + 24 = 42
}

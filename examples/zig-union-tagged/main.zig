// dotcc Zig front-end — `union(SomeEnum)`: a tagged union with an EXPLICIT, named tag enum
// (Milestone R).
//
// Milestone D3 added the auto-inferred-tag form `union(enum) { … }`. This adds the form where the
// discriminant is an existing, named enum: `union(Kind) { … }`. The variant set must correspond to
// the enum's members, and the tag VALUE of each variant is that enum member's value — so a non-zero
// or out-of-order enum (`Kind` below uses 1/2/4) drives the discriminant, proving the named enum is
// the tag (not a synthesized 0-based one). It reuses dotcc's tagged-union lowering 1:1 — an outer
// `{ __tag, __payload }` struct, switch on `__tag`, payload capture `|x|` — only the tag enum source
// changes.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-union-tagged/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Kind = enum(u8) { num = 1, small = 2, flag = 4 };

const Value = union(Kind) {
    num: i32,
    small: u8,
    flag: bool,
};

fn score(v: Value) u8 {
    switch (v) {
        .num => |x| {
            return @intCast(x);
        },
        .small => |y| {
            return y;
        },
        .flag => |z| {
            return if (z) 100 else 0;
        },
    }
}

pub fn main() u8 {
    const a: Value = .{ .num = 30 };
    const b: Value = .{ .small = 12 };
    const total = score(a) + score(b); // 30 + 12 = 42

    // The tag value comes from the named enum: Kind.flag == 4.
    _ = printf("flagtag=%d total=%u\n", @as(c_int, @intFromEnum(Kind.flag)), @as(c_uint, total));
    return total; // 42
}

// dotcc Zig front-end — NESTED containers as full containers (road-to-zig-std G3).
//
// zig containers nest, and std leans on it constantly: `std.fmt.Number` is a struct whose first field
// is `mode: Mode = .decimal`, where `Mode` is an enum declared INSIDE `Number`, AFTER that field, with
// a method of its own. That one field was the wall `std.fmt.bufPrint` hit first.
//
// dotcc now registers every nested container exactly like a top-level one — any kind, with methods,
// consts and further nesting — under a parent-mangled name (`Number__Mode`), so two parents can each
// nest a `Kind` without colliding. What makes it a NESTED type is only how its name resolves:
//
//   - plainly, through the lexical parent chain, from anything inside the parent — a sibling field's
//     type, a method, a grandchild (`B` below names its uncle `K`);
//   - qualified, from outside: `Number.Mode`, `A.B.C`, `Number.Mode.decimal`, `A.P.two()`.
//
// One thing zig insists on and dotcc does not: declarations may not sit BETWEEN fields. Every
// container here keeps its fields first.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-nested-containers/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// std.fmt.Number, trimmed to the members this needs.
const Number = struct {
    mode: Mode = .decimal,
    width: ?usize = null,

    pub const Mode = enum {
        decimal,
        hex,

        pub fn base(mode: Mode) u8 {
            return switch (mode) {
                .decimal => 10,
                .hex => 16,
            };
        }
    };
};

const A = struct {
    b: B,

    pub const K = enum { x, y };
    pub const B = struct {
        k: K, // an UNCLE, named plainly
        c: C,

        pub const C = struct { v: u8 };
    };
    pub const P = struct {
        pub const EXTRA: u8 = 4;
        pub fn two() u8 {
            return 2;
        }
    };
};

pub fn main() u8 {
    const n: Number = .{ .mode = .hex };
    const d: Number.Mode = Number.Mode.decimal;
    _ = printf("bases: hex=%d decimal=%d\n", @as(c_int, n.mode.base()), @as(c_int, d.base()));

    const a: A = .{ .b = .{ .k = .y, .c = .{ .v = 10 } } };
    const c: A.B.C = a.b.c;
    _ = printf("A.B.C.v=%d A.P.EXTRA=%d A.P.two()=%d\n", @as(c_int, c.v), @as(c_int, A.P.EXTRA), @as(c_int, A.P.two()));

    // 16 + 10 + 10 + 4 + 2 = 42
    var total: u8 = n.mode.base();
    total += d.base();
    total += c.v;
    if (a.b.k == .y) total += A.P.EXTRA;
    total += A.P.two();
    return total;
}

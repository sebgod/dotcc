// dotcc Zig front-end — by-reference capture `|*x|` (Milestone M, part 4).
//
// A by-reference capture binds the loop element / matched union payload to a POINTER into the source,
// so writing through it (`x.* = …`) mutates the original storage. dotcc lowers:
//   for (s) |*e| { … }                 →  for (…) { T* e = &s.Ptr[__i]; … }
//   switch (u) { .v => |*p| { … } }    →  case …: { T* p = &u.__payload.v; … }
// i.e. an `&` into addressable storage (a slice element / a union payload field). Mutating `e.*` /
// `p.*` writes back. (Deferred: by-ref capture on an optional / error-union `if`/`while`, and a
// multi-variant capture prong.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-byref-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Box = union(enum) { i: i32, f: i32 };

pub fn main() u8 {
    var sum: i32 = 0;

    // for-slice BY-REFERENCE: double each element in place via `e.*`.
    var arr = [_]i32{ 3, 4, 5, 6 };
    const s: []i32 = arr[0..4];
    for (s) |*e| {
        e.* = e.* * 2; // 6, 8, 10, 12
    }
    for (s) |e| {
        sum += e; // 36
    }

    // switch BY-REFERENCE: mutate the active union payload in place via `p.*`.
    var b: Box = .{ .i = 0 };
    switch (b) {
        .i => |*p| {
            p.* = 6;
        },
        .f => |*p| {
            p.* = 0;
        },
    }
    // read it back (by-value capture) to confirm the mutation persisted.
    switch (b) {
        .i => |v| {
            sum += v; // +6
        },
        .f => |v| {
            sum += v;
        },
    }

    _ = printf("sum=%d\n", sum); // 36 + 6 = 42
    return @as(u8, @intCast(sum));
}

// dotcc Zig front-end — a type-returning generic that computes a TYPE via a captured-`if` fold.
//
// `Store(comptime T: type, comptime cap: ?u8) type` has a MULTI-statement body: it computes a type
// alias with a captured-`if` on the comptime optional `cap`, then returns a struct that uses it.
// The `if (cap) |n| [n]T else []T` FOLDS at instantiation: a payload argument selects the then-type
// (`[n]T`, a fixed array, with `n` bound to the compile-time value), a `null` argument selects the
// else-type (`[]T`, a slice). Each instantiation reifies its own struct (`Store__u8_3`,
// `Store__u8_optnull`).
//
// This is exactly the shape of `std.ArrayList`'s `Aligned(T, alignment)`:
//   const Slice = if (alignment) |a| ([]align(a.toByteUnits()) T) else []T;
// — the S4 arc's target for compiling std.ArrayList from source (road-to-zig-std S4b pt2 / S4c).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime-type-fold/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

fn Store(comptime T: type, comptime cap: ?u8) type {
    // The captured-if folds to a TYPE at instantiation: [n]T for a payload `cap`, []T for `null`.
    const Slice = if (cap) |n| [n]T else []T;
    return struct { data: Slice, len: usize };
}

pub fn main() u8 {
    // Payload instance: `cap = 3` → data is a fixed array `[3]u8`.
    var arr: Store(u8, 3) = undefined;
    arr.data[0] = 20;
    arr.data[1] = 0;
    arr.data[2] = 0;
    arr.len = 1;

    // Null instance: `cap = null` → data is a slice `[]u8` (the std.ArrayList path).
    var buf = [_]u8{ 22, 0 };
    const sl: Store(u8, null) = .{ .data = &buf, .len = 2 };

    return arr.data[0] + sl.data[0]; // 20 + 22 = 42
}

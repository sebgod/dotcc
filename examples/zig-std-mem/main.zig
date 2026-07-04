// dotcc Zig front-end — curated std.mem helpers + @memcpy/@memset over slices.
//
// dotcc does not model `std` in general, but lowers the most common slice utilities onto faithful
// runtime primitives (DotCC.Libc/ZigMem.cs): `std.mem.eql` (element-wise slice equality),
// `std.mem.copyForwards` (forward element copy), and the `@memcpy` / `@memset` builtins. These
// rely on Zig's `*[N]T` → `[]T` coercion, so a `&array` promotes to a slice at a `[]T` sink.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-std-mem/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

const std = @import("std");

extern fn printf(format: [*c]const u8, ...) c_int;

fn countL(s: []const u8) usize {
    return s.len;
}

pub fn main() u8 {
    const a = [_]u8{ 1, 2, 3 };
    const b = [_]u8{ 1, 2, 3 };
    const c = [_]u8{ 1, 2, 4 };

    // std.mem.eql — equal length AND identical contents. (`if` in value position picks 1 / 0.)
    const eqAB: c_int = if (std.mem.eql(u8, &a, &b)) 1 else 0; // 1
    const eqAC: c_int = if (std.mem.eql(u8, &a, &c)) 1 else 0; // 0

    // `&array` coerces to a `[]const u8` slice parameter (`*[N]T` → `[]T`).
    const len: c_int = @intCast(countL(&a)); // 3

    // std.mem.copyForwards — copy source.len elements into dst, front to back.
    var dst = [_]u8{ 0, 0, 0 };
    std.mem.copyForwards(u8, &dst, &a); // dst = { 1, 2, 3 }

    // @memset — fill every element; @memcpy — copy equal-length slices.
    var buf = [_]u8{ 9, 9, 9, 9 };
    @memset(&buf, 7); // buf = { 7, 7, 7, 7 }
    var b2 = [_]u8{ 0, 0, 0 };
    @memcpy(&b2, &a); // b2 = { 1, 2, 3 }

    _ = printf("eql=%d%d len=%d copy=%d%d%d set=%d cpy=%d%d%d\n", eqAB, eqAC, len, @as(c_int, dst[0]), @as(c_int, dst[1]), @as(c_int, dst[2]), @as(c_int, buf[0]), @as(c_int, b2[0]), @as(c_int, b2[1]), @as(c_int, b2[2]));

    return 42;
}

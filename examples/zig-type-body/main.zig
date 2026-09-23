// dotcc Zig front-end — a type-returning body EVALUATED at comptime — the W4 lift (road-to-zig-std).
//
// A zig function that returns `type` is a program the compiler runs. Wall-plan W4 taught dotcc the
// commonest shape, `return struct { … };`, and refused the rest. The rest turned out to matter: in
// the pinned std, 28 type functions DELEGATE to another one — `std.ArrayList(T)` is literally
// `return array_list.Aligned(T, null);` — 16 more `return switch (@typeInfo(T)) { … }`, and several
// open with a comptime `if` that returns early. So the body is now walked as comptime code:
//
//   - a `const` binds a TYPE alias or a comptime VALUE (`Log2` below computes a width from one);
//   - an `if` or `switch` whose condition folds contributes ONLY its taken arm;
//   - the first `return` reached is the answer — `return struct {…}` reifies one, anything else IS
//     the result.
//
// Two things here are worth watching:
//
//   - A delegating function makes no new type. `List(u8)`, `Aligned(u8, null)` and `Aligned(u8, 1)`
//     are ONE type in zig — that is why `const same: Aligned(u8, 1) = list;` type-checks at all —
//     and dotcc resolves all three to the one struct it reifies.
//
//   - `Log2(u64)` is `@Int(.unsigned, 16 - @clz(bits - 1))` with `bits: u16`. zig has no integer
//     promotion, so `bits - 1` is a `u16` and `@clz` counts in 16 bits (clz(63) = 10), giving a `u6`.
//     Count in C's promoted 32 bits instead and you get 26, a width of -10, and no type at all.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-type-body/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// `array_list.Aligned`'s own opening: a known alignment that changes nothing delegates to the
// default instance instead of reifying a second, identical type.
fn Aligned(comptime T: type, comptime alignment: ?u8) type {
    if (alignment) |a| {
        if (a == 1) return Aligned(T, null);
    }
    return struct {
        items: [4]T,
        len: usize,

        const Self = @This();

        pub fn push(self: *Self, v: T) void {
            self.items[self.len] = v;
            self.len += 1;
        }
    };
}

// `std.ArrayList` in one line.
fn List(comptime T: type) type {
    return Aligned(T, null);
}

// `std.meta.Child`, verbatim in shape: pick the kind, read its child type off the payload.
fn Child(comptime T: type) type {
    return switch (@typeInfo(T)) {
        .pointer => |info| info.child,
        .optional => |info| info.child,
        .array => |info| info.child,
        else => @compileError("Child wants a pointer, an optional or an array"),
    };
}

// `std.math.Log2Int`, verbatim in shape: the smallest unsigned type that can index T's bits.
fn Log2(comptime T: type) type {
    if (T == comptime_int) return comptime_int;
    const bits: u16 = @typeInfo(T).int.bits;
    const log2_bits = 16 - @clz(bits - 1);
    return @Int(.unsigned, log2_bits);
}

pub fn main() u8 {
    var list: List(u8) = .{ .items = undefined, .len = 0 };
    list.push(20);
    list.push(10);
    // The same type under another spelling — accepted by both compilers, or neither would build this.
    const same: Aligned(u8, 1) = list;
    _ = printf("list: len=%d first=%d\n", @as(c_int, @intCast(same.len)), @as(c_int, same.items[0]));

    const c: Child(*u16) = 7;
    const o: Child(?[3]u8) = [_]u8{ 1, 2, 2 };
    _ = printf("child: %d, %d\n", @as(c_int, c), @as(c_int, o[2]));

    _ = printf("log2: u64 -> %d bits, u8 -> %d bits\n",
        @as(c_int, @bitSizeOf(Log2(u64))), @as(c_int, @bitSizeOf(Log2(u8))));

    // 20 + 10 + 7 + 2 + (6 - 3) = 42
    return @intCast(same.items[0] + same.items[1] + c + o[2] + @bitSizeOf(Log2(u64)) - @bitSizeOf(Log2(u8)));
}

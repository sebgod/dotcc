// dotcc Zig front-end — overflow-detecting arithmetic builtins.
//
// `@addWithOverflow(a, b)` (and `-`/`*`/`shl` siblings) return Zig's `struct { T, u1 }`: the
// WRAPPED result plus a 0/1 overflow flag. dotcc lowers each to `ZigMath.<op>WithOverflow<T>`
// returning a C# `(T, byte)` tuple, so the result destructures (`const r, const o = …`) or
// indexes (`t[0]`/`t[1]`) through the normal tuple path. The flag is computed exactly in a
// 128-bit accumulator (a 128-bit operand is a loud cut — no wider accumulator to detect its
// overflow). This is what `std.ArrayList`'s `addOrOom` uses to turn a capacity overflow into
// `error.OutOfMemory` — see `addOrOom` below.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-overflow-ops/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

/// The exact shape from `std.ArrayList`: add, and report overflow as an error.
fn addOrOom(a: usize, b: usize) error{OutOfMemory}!usize {
    const result, const overflow = @addWithOverflow(a, b);
    if (overflow != 0) return error.OutOfMemory;
    return result;
}

pub fn main() u8 {
    // Destructure the tuple directly.
    const sum, const add_ovf = @addWithOverflow(@as(u8, 200), 100); // .{ 44, 1 }
    // Or bind the whole tuple and index it.
    const mul = @mulWithOverflow(@as(u8, 20), 20); // .{ 144, 1 }
    const shl = @shlWithOverflow(@as(u8, 3), 7); // 3 << 7 = 384 -> .{ 128, 1 }

    // addOrOom detects the wrap-around and yields the error; a fitting add yields the value.
    const ok = addOrOom(40, 2) catch return 1; // 42
    const capped = addOrOom(0xFFFFFFFFFFFFFFFF, 1) catch 7; // overflow -> caught 7
    if (capped != 7) return 2;

    // sum(44) + flags(1+1+1) - ok-based check: prove every piece lines up, then return `ok` (42).
    const flags: u8 = @as(u8, add_ovf) + @as(u8, mul[1]) + @as(u8, shl[1]); // 3
    if (sum != 44 or flags != 3) return 3;

    return @intCast(ok); // 42
}

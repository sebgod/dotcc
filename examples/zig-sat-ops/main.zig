// dotcc Zig front-end — saturating arithmetic `+| -| *|` (+ compound `+|= -|= *|=`) (Milestone P, part 2).
//
// Where `+%` WRAPS on overflow, `+|` CLAMPS to the operand type's range — `u8: 200 +| 100 == 255`,
// `i8: 100 +| 100 == 127`, `i8: -100 -| 100 == -128`, and unsigned subtraction floors at 0. A clamp
// has no native C# operator, so dotcc routes these through the spliced `ZigMath.Sat{Add,Sub,Mul}<T>`
// runtime, which widens both operands to a 128-bit accumulator, performs the EXACT op there, and
// clamps to `[T.min, T.max]` before truncating back — exception-free and correct for every width.
// The clamp is at the OPERAND width even when widened: `(250 +| 10)` is 255 (at u8), not 260 (at u32).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-sat-ops/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    var x: u8 = 200;
    x +|= 100; // saturating add-assign: 300 -> 255
    if (x != 255) return 1;

    var y: u8 = 5;
    y -|= 10; // saturating sub-assign underflow: -> 0
    if (y != 0) return 2;

    var k: u8 = 100;
    k *|= 100; // saturating mul-assign: 10000 -> 255
    if (k != 255) return 3;

    // signed saturation, both ends.
    var s: i8 = 100;
    s +|= 100; // 200 -> 127
    if (s != 127) return 4;
    var n: i8 = -100;
    n -|= 100; // -200 -> -128
    if (n != -128) return 5;

    // The clamp is at the u8 operand width (260 -> 255), THEN widened to u32 — not 260.
    const a: u8 = 250;
    const b: u8 = 10;
    const w: u32 = a +| b;
    if (w != 255) return 6;

    _ = printf("x=%u y=%u k=%u s=%d n=%d w=%u\n", x, @as(c_uint, y), @as(c_uint, k), @as(c_int, s), @as(c_int, n), w);

    const base: u8 = 40;
    return base +| 2; // 42 (no saturation)
}

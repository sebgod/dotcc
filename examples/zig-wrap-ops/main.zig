// dotcc Zig front-end — wrapping arithmetic `+% -% *%` (+ compound `+%= -%= *%=`) (Milestone P, part 1).
//
// Zig's plain `+`/`-`/`*` TRAP on overflow in safe build modes; `+%`/`-%`/`*%` are the explicit
// two's-complement WRAPPING escape hatches (hashes, checksums, fixed-point). dotcc has no integer
// promotion to model, so the wrap happens at the OPERAND width: `u8 +% u8` wraps at 8 bits. In the
// emitted C#'s unchecked context a narrowing cast truncates, so a sub-`int` operand width gets a
// `(byte)`/`(short)` cast back; `int`-and-wider already wrap natively. The wrap is at the operand
// width even when the result is then widened — `(250 +% 10)` is 4 (at u8), not 260 (at u32).
// (dotcc does NOT model the safe-mode trap on plain `+`, so `+%` and `+` behave identically here.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-wrap-ops/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    var x: u8 = 200;
    x +%= 100; // wrapping add-assign: 300 -> 44
    x -%= 2; // 42

    var k: u8 = 16;
    k *%= 16; // wrapping mul-assign: 256 -> 0
    if (k != 0) return 1;

    const z: u8 = 0;
    const u: u8 = z -% 2; // wrapping sub underflow: 0 -% 2 -> 254
    if (u != 254) return 2;

    // The wrap is at the u8 operand width (260 -> 4), THEN widened to u32 — not 260.
    const a: u8 = 250;
    const b: u8 = 10;
    const w: u32 = a +% b;
    if (w != 4) return 3;

    _ = printf("x=%u k=%u u=%u w=%u\n", x, @as(c_uint, k), @as(c_uint, u), w);
    return x; // 42
}

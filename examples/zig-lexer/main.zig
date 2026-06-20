// dotcc Zig front-end — lexer & literal completeness (Milestone I).
//
// Radix integer prefixes (`0x` hex, `0o` octal, `0b` binary) and `_` digit separators; hex
// float literals (`0x1.8p3`, converted to a decimal — C# has no hex-float syntax); the `\"`
// escaped quote and `\u{…}` unicode escape inside a quoted string; and a `\\`-prefixed
// multiline string (its lines joined with `\n`, escapes NOT processed).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-lexer/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    // Radix prefixes + `_` separators — all decode to the same value.
    const hex: u32 = 0xFF_FF; // 65535
    const oct: u32 = 0o17; // 15
    const bin: u32 = 0b1010; // 10
    _ = printf("hex=%u oct=%u bin=%u\n", hex, oct, bin);

    // Hex float (= 1.5 * 2^3 = 12.0) and an underscored decimal float.
    const hf: f64 = 0x1.8p3;
    const df: f64 = 1_000.5;
    _ = printf("hf=%.1f df=%.1f\n", hf, df);

    // Escaped quote and a unicode escape ('!' = U+0021) inside a quoted string.
    _ = printf("quote=\" bang=\u{21}\n");

    // A multiline string — lines joined with `\n`, no trailing newline.
    _ = printf(
        \\line one
        \\line two
    );
    _ = printf("\n");

    // 0x14 (20) + 0o32 (26) - 0b100 (4) = 42.
    return 0x14 + 0o32 - 0b100;
}

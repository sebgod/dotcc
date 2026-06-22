// dotcc Zig front-end — Milestone ß ("sharp-s"): 128-bit integers `i128` / `u128`.
//
// dotcc has no native 128-bit integer; these lower to C# `System.Int128` / `System.UInt128`
// (BCL primitives, so all arithmetic — `*`, `/`, shifts, comparisons — comes for free). The
// values below genuinely exceed 64 bits (2^80, 2^100), so a 64-bit lowering would truncate
// them — proving the type is really 128-bit — and are reduced to an observable byte exit code.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-i128/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    // 2^40 * 2^40 = 2^80 — overflows u64, needs u128. Its top 64 bits hold 2^16 = 65536.
    const a: u128 = @as(u128, 1) << 40;
    const wide: u128 = a * a;
    const hi: u64 = @intCast(wide >> 64); // 65536

    // Signed i128: -(2^100) arithmetic-shifted right by 100 is -1 (the sign is preserved at 128 bits).
    const big: i128 = -(@as(i128, 1) << 100);
    const neg: i64 = @intCast(big >> 100); // -1

    const code: u64 = (hi / 65536) + @as(u64, @intCast(-neg)) + 40; // 1 + 1 + 40 = 42
    _ = printf("hi=%llu neg=%lld\n", hi, neg);
    return @intCast(code);
}

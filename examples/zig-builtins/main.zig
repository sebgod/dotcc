// dotcc Zig front-end — result-location cast builtins (Milestone J).
//
// @intCast / @truncate / @floatFromInt / @intFromFloat / @floatCast / @bitCast / @ptrCast +
// @alignCast / @enumFromInt all take their TARGET type from the result location — the typed
// binding / return / call argument the value flows into — not an explicit type argument (that
// is what @as carries). Plus the constant @sizeOf. dotcc threads the result-location type (the
// "sink") into builtin lowering, mapping each onto the C cast IR (@bitCast → Unsafe.BitCast).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-builtins/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Color = enum(u8) { red, green, blue };

pub fn main() u8 {
    // @intCast narrows a wide usize to u8 (the recurring for-index limitation, now expressible).
    const wide: usize = 200;
    const narrow: u8 = @intCast(wide);

    // @truncate keeps the low byte of a u32 (0xFF = 255).
    const big: u32 = 0x1234_56FF;
    const low: u8 = @truncate(big);

    // int -> f64 -> f32 -> int round trip (200).
    const f: f64 = @floatFromInt(narrow);
    const g: f32 = @floatCast(f);
    const back: u8 = @intFromFloat(g);

    // @bitCast reinterprets 1.0f's bits; its biased exponent (bits >> 23) is 127.
    const bits: u32 = @bitCast(@as(f32, 1.0));
    const exp: u8 = @truncate(bits >> 23);

    // @enumFromInt + @intFromEnum round trip through Color.blue (= 2).
    const c: Color = @enumFromInt(@as(u8, 2));
    const ci: u8 = @intFromEnum(c);

    // @ptrCast + @alignCast reinterpret a u32's storage as bytes and back (low byte = 42).
    var store: u32 = 0;
    store = 42;
    const pb: *u8 = @ptrCast(&store);
    const pw: *u32 = @ptrCast(@alignCast(pb));
    const round: u8 = @intCast(pw.* & 0xFF);

    _ = printf("narrow=%d low=%d back=%d exp=%d ci=%d round=%d\n",
        @as(c_int, narrow), @as(c_int, low), @as(c_int, back),
        @as(c_int, exp), @as(c_int, ci), @as(c_int, round));

    // @sizeOf(u32) (= 4) * 10 + 2 = 42, narrowed from usize to the u8 return.
    const total: usize = @sizeOf(u32) * 10 + 2;
    return @intCast(total);
}

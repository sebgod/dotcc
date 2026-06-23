// dotcc Zig front-end — `inline for` comptime loop UNROLLING — Milestone T, part 3.
//
// `inline for (lo..hi) |i| body` is a COUNTED loop the compiler UNROLLS: the body is replicated once
// per index, with `i` a compile-time constant in each copy, so no runtime `for` survives. dotcc lowers
// it to N straight-line `{ const i = v; body }` blocks. Because that's plain straight-line IR, the
// same construct works in BOTH places it appears below:
//
//   - inside `buildSquares()`, which is `comptime`-called — the compile-time interpreter walks the
//     unrolled copies to fold the whole table into a literal (`[0,1,4,9,16]`), no call at runtime;
//   - inside `main()` at RUNTIME — the copies execute in order, accumulating the sum.
//
// This is the milestone's upper edge: value-comptime that EMITS IR (N body copies) rather than only
// folding a constant — but still no types involved. V1 unrolls only the counted range form; an
// `inline while`, an `inline for` over a slice/array, a non-constant bound, or a `break`/`continue`
// inside the body are clear deferred errors.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-inline-for/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "sum=30 sq4=16")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A lookup table built with `inline for`. Called at comptime below, so the whole loop runs in the
// interpreter and the result is baked in as a literal — but the SAME function would unroll into
// straight-line stores if called at runtime.
fn buildSquares() [5]u32 {
    var t: [5]u32 = undefined;
    inline for (0..5) |i| {
        t[i] = @intCast(i * i);
    }
    return t;
}

pub fn main() u8 {
    const sq = comptime buildSquares(); // folded to a [0,1,4,9,16] literal — no buildSquares() call

    // Runtime `inline for`: unrolls into `sum += sq[0]; sum += sq[1]; …` straight-line accumulation.
    var sum: u32 = 0;
    inline for (0..5) |i| {
        sum += sq[i];
    }

    _ = printf("sum=%u sq4=%u\n", sum, sq[4]);
    return @intCast(sum + 12); // 30 + 12 = 42
}

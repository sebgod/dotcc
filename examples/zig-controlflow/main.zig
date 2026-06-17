// dotcc Zig front-end — control flow (Milestone C): while continue-expression,
// `break` / `continue`, `switch`, and the range `for`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-controlflow/main.zig --emit=file -o out.cs
//             dotnet run out.cs                          # -> "sum=18 hits=3 class=200", exit 18
//   real zig: zig build-exe main.zig -lc && ./main       # -> same
extern fn printf(format: [*c]const u8, ...) c_int;

// `switch` as a statement: single value, multi-value prong, and `else` (the default).
// Zig switch has NO fall-through — each prong stands alone.
fn classify(x: u8) u8 {
    var r: u8 = 0;
    switch (x) {
        0 => { r = 100; },
        1, 2, 3 => { r = 200; },
        else => { r = 0; },
    }
    return r;
}

pub fn main() u8 {
    // `while (cond) : (cont)` + `break` + `continue` — the cont (`i = i + 1`) runs on
    // `continue` too, so i still advances past the skipped value.
    var sum: u8 = 0;
    var i: u8 = 0;
    while (i < 10) : (i = i + 1) {
        if (i == 3) continue;   // skip 3
        if (i == 7) break;      // stop at 7
        sum = sum + i;          // 0+1+2 +4+5+6 = 18
    }

    // range `for` with the usize loop index used in a comparison.
    var hits: u8 = 0;
    for (0..5) |k| {
        if (k >= 2) { hits = hits + 1; }   // k = 2,3,4 → 3
    }

    const c = classify(2);   // the `1, 2, 3` prong → 200

    _ = printf("sum=%d hits=%d class=%d\n", @as(c_int, sum), @as(c_int, hits), @as(c_int, c));
    return sum;   // 18
}

// Generic functions via comptime VALUE parameters (wall-plan W3a) — call-site monomorphization.
// A `comptime` parameter has no runtime storage; a call instantiates a SPECIALIZED body per resolved
// value (C++-template-style), emitted under a mangled name (`addN__10`, `powi__10`, `fib__9`) and
// memoized so a repeat call reuses it. The comptime value is baked into the body as a literal.
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-comptime-param/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

// The comptime N is added as a baked-in literal — addN(10, x) and addN(100, x) are distinct instances.
fn addN(comptime N: i32, x: i32) i32 {
    return x + N;
}

// The comptime bound n specializes the loop trip count; the loop still runs at runtime.
fn powi(comptime n: u32, x: i64) i64 {
    var r: i64 = 1;
    var i: u32 = 0;
    while (i < n) : (i = i + 1) {
        r = r * x;
    }
    return r;
}

// A recursive generic — each `fib(n - 1)` folds to a constant comptime arg, transitively
// instantiating fib__9, fib__8, … down to the base cases. Exercises the worklist + memoization.
fn fib(comptime n: u32) u64 {
    if (n < 2) return n;
    return fib(n - 1) + fib(n - 2);
}

pub fn main() void {
    _ = printf("addN10=%d addN100=%d\n", addN(10, 5), addN(100, 5));
    _ = printf("pow2_10=%lld pow3_4=%lld\n", powi(10, 2), powi(4, 3));
    _ = printf("fib10=%llu\n", fib(10));
}

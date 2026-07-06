// Generic functions via comptime TYPE parameters (wall-plan W3b) — the second half of call-site
// monomorphization. A `comptime T: type` parameter makes the later parameter / return types depend
// on `T`, so — unlike a comptime VALUE parameter (W3a) — the SIGNATURE cannot be lowered once at
// template time: each call resolves the type argument to a concrete type, seeds `T`, and emits a
// SPECIALIZED body under a mangled name keyed by the RESOLVED type (`maxOf__i32`, `maxOf__f64`, …).
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-comptime-type-param/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

// maxOf(i32, ·) → `int maxOf__i32(int, int)`; maxOf(f64, ·) → `double maxOf__f64(double, double)`.
// The parameter AND return types are the concrete type argument — a per-instance signature.
fn maxOf(comptime T: type, a: T, b: T) T {
    return if (a > b) a else b;
}

// A second type-parameterized function, over i64 and f32 here — each instantiation is independent.
fn addOf(comptime T: type, a: T, b: T) T {
    return a + b;
}

// `T` resolves INSIDE the body too: @sizeOf(T) folds against the seeded type (4 for i32, 8 for i64/f64).
fn sizeOfType(comptime T: type) i32 {
    return @intCast(@sizeOf(T));
}

// An alias for a type keys the SAME instance as the underlying type (`maxOf(I, …)` ≡ `maxOf(i32, …)`).
const I = i32;

pub fn main() void {
    _ = printf("max_i32=%d max_f64=%.1f\n", maxOf(I, 3, 7), maxOf(f64, 2.5, 1.5));
    _ = printf("add_i64=%lld add_f32=%.1f\n", addOf(i64, 100, 5), @as(f64, addOf(f32, 1.5, 2.0)));
    _ = printf("sz_i32=%d sz_i64=%d sz_f64=%d\n", sizeOfType(i32), sizeOfType(i64), sizeOfType(f64));
}

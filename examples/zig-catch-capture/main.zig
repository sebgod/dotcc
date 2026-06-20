// dotcc Zig front-end — `catch |e|` capture + a lazy / side-effecting `catch` fallback
// (Milestone N, part 3).
//
// `a catch b` yields the error union's payload on success, else the fallback `b`. dotcc keeps the
// eager `ErrUnion.Catch(a, b)` for a simple, side-effect-free `b`, but a CAPTURING `a catch |e| b`
// (binds the error to `e` for the fallback) or a SIDE-EFFECTING `b` needs a LAZY lowering — `b` must
// run only on error — so dotcc hoists the union to a single-eval temp and lowers it to
//   __cE.IsErr ? <b, with `e` bound to the flat error code> : __cE.Value
// (a ternary, so `b` is lazy). Supported as a `const`/`var` initializer (the capture bind is a
// statement). A simple `a catch 0` keeps the eager helper.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-catch-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

fn mayBool(ok: bool) !bool {
    if (ok) return true;
    return error.Bad;
}

fn mayInt(ok: bool) !i32 {
    if (ok) return 7;
    return error.Bad;
}

// A non-trivial (call) fallback — `catch dflt()` must run it ONLY on the error path.
fn dflt() i32 {
    return 13;
}

pub fn main() u8 {
    var sum: i32 = 0;

    // `catch |e| b` capture — the error binds to `e`; the bool fallback uses it (`e == error.Bad`).
    const a = mayBool(true) catch |e| (e == error.Bad); // success → the payload `true`
    const b = mayBool(false) catch |e| (e == error.Bad); // error → `error.Bad == error.Bad` = true
    if (a) sum += 10; // +10
    if (b) sum += 12; // +12

    // side-effecting (call) fallback — lazy: `dflt()` runs only on the error path.
    const c = mayInt(true) catch dflt(); // success → 7 (dflt NOT called)
    const d = mayInt(false) catch dflt(); // error → dflt() = 13
    sum += c; // +7
    sum += d; // +13

    _ = printf("sum=%d\n", sum); // 10 + 12 + 7 + 13 = 42
    return @as(u8, @intCast(sum));
}

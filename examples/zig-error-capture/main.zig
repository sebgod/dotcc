// dotcc Zig front-end — error-union payload / error capture in `if` (Milestone M, part 3).
//
// `if (eu) |x| { … } else |e| { … }` binds an error union's SUCCESS payload to `x` in the
// then-branch (taken when the union holds a value) and the ERROR to `e` in the else-branch. dotcc
// lowers it to a value inspection of the runtime `ErrUnion<T>` — `if (Cond.B(__cap.IsErr)) { var e =
// __cap.Code; … } else { var x = __cap.Value; … }` — NOT a propagating `try`, so the error is handled
// here and never reaches the function boundary.
//
// An error union REQUIRES handling the error: Zig forbids a plain `else` (and forbids `_ = e;`) — you
// either name it `|e|` or discard it with `|_|`. This example uses the payload capture `|x|` plus
// `else |_|` (the both-compiler-valid subset). The NAMED `|e|` binding also lowers (see the unit emit
// pins), but OPERATING on the bound error — `e == error.Bad`, `@errorName(e)`, propagation — awaits the
// error-set milestone (today `e` is the erased `ushort` code), so it's left out of this round-trip.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-error-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// Succeeds with `v` when `ok`, else fails with `error.Bad` — a plain `!i32` producer.
fn tryVal(ok: bool, v: i32) !i32 {
    if (ok) return v;
    return error.Bad;
}

pub fn main() u8 {
    var sum: i32 = 0;

    // success → the payload is captured in the then-branch.
    if (tryVal(true, 20)) |x| {
        sum += x; // +20
    } else |_| {
        sum += 100;
    }

    // failure → the else-branch runs (the error is discarded via `|_|`).
    if (tryVal(false, 99)) |x| {
        sum += x;
    } else |_| {
        sum += 22; // +22
    }

    _ = printf("sum=%d\n", sum); // 20 + 22 = 42
    return @as(u8, @intCast(sum));
}

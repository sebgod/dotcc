// dotcc Zig front-end — error values as comparable values (Milestone N, part 1).
//
// An `error.Foo` is now a first-class VALUE, not only the operand of `return error.Foo;`. dotcc
// erases the named error set into one flat global code space, so each `error.Foo` name maps to a
// stable `ushort` code (shared program-wide) and an `error.Foo` value lowers to that code, typed
// `CType.ErrorSet` (rendered C# `ushort`). Equal codes mean equal errors, so `e == error.Foo`
// compares codes. This also un-erases the Milestone M part-3 `else |e|` capture: the bound error is
// now an error-set value, so a USED `|e|` (compared against a named error) is finally valid in BOTH
// compilers — exactly what the part-3 oracle couldn't exercise until error values landed.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-error-value/main.zig --emit=file -o out.cs
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

    // a USED captured error, compared against a named error value (the part-3 payoff).
    if (tryVal(false, 99)) |x| {
        sum += x;
    } else |e| {
        if (e == error.Bad) {
            sum += 20; // +20  (tryVal failed with error.Bad)
        } else {
            sum += 100;
        }
    }

    // success path — the error branch's comparison is not reached.
    if (tryVal(true, 12)) |x| {
        sum += x; // +12
    } else |e| {
        if (e == error.Bad) sum += 1;
    }

    // a bare error value bound to a const, then compared.
    const want = error.Bad;
    if (want == error.Bad) {
        sum += 10; // +10
    }
    if (want == error.Other) {
        sum += 100;
    }

    _ = printf("sum=%d\n", sum); // 20 + 12 + 10 = 42
    return @as(u8, @intCast(sum));
}

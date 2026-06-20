// dotcc Zig front-end — `switch` on an error value (Milestone N, part 2).
//
// An error value is a comparable value (part 1), so `switch (e) { error.Foo => …, else => … }`
// just works: dotcc erases the named set into one flat global code space, so an error value IS its
// `ushort` code and the switch lowers to an ORDINARY C# integer switch on that code — each
// `error.Foo` prong becomes a `case <code>:` label and `else` becomes `default:`. No new lowering
// over part 1 (the `CType.ErrorSet`-renders-`ushort` marker routes straight through the integer
// `switch` machinery). The error is commonly captured from an `else |e|` first.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-error-switch/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// `anyerror!i32`: the OPEN error set (so the switch's `else` is required and reachable). Fails with
// `error.Zero` on 0, `error.Negative` on a negative, else succeeds with the value.
fn classify(n: i32) anyerror!i32 {
    if (n == 0) return error.Zero;
    if (n < 0) return error.Negative;
    return n;
}

// Capture the error (or value) and fold a score into `sum` via a `switch` on the captured error.
fn score(n: i32, sum: *i32) void {
    if (classify(n)) |v| {
        sum.* += v;
    } else |e| {
        switch (e) {
            error.Zero => {
                sum.* += 20;
            },
            error.Negative => {
                sum.* += 5;
            },
            else => {
                sum.* += 1;
            },
        }
    }
}

pub fn main() u8 {
    var sum: i32 = 0;
    score(0, &sum); // classify → error.Zero → +20
    score(-3, &sum); // classify → error.Negative → +5
    score(17, &sum); // classify → 17 (success) → +17
    _ = printf("sum=%d\n", sum); // 20 + 5 + 17 = 42
    return @as(u8, @intCast(sum));
}

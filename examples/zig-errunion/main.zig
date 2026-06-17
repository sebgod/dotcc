// dotcc Zig front-end — error unions (`E!T`): `!T` returns, `error.X`, `try`, `catch`.
//
// Milestone B2. A `!T` function returns an error union — either a payload or an error.
//   - `return error.Foo;`   the error path (V1 erases the error SET: a flat global space).
//   - `return value;`       the success path (the payload).
//   - `try e`               unwrap the payload, or PROPAGATE the error out of the current
//                           `!T` function. dotcc lowers this exactly like the C front-end's
//                           setjmp/longjmp: a private exception thrown by `try`, caught at the
//                           function boundary and turned back into an error return — the one
//                           construct that needs early-return-out-of-an-expression.
//   - `e catch fallback`    the payload on success, else the fallback (no propagation).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-errunion/main.zig --emit=file -o out.cs
//             dotnet run out.cs                          # -> "ok=42 bad=99", exit 42
//   real zig: zig build-exe main.zig -lc && ./main       # -> same
extern fn printf(format: [*c]const u8, ...) c_int;

fn parse(x: u8) !u8 {
    if (x == 0) return error.Zero;   // the error path
    return x + 1;                    // the success payload
}

fn doubled(x: u8) !u8 {
    const v = try parse(x);          // unwrap, or propagate parse's error to our caller
    return v * 2;
}

pub fn main() u8 {
    const ok = doubled(20) catch 0;  // parse(20)=21 -> *2 = 42
    const bad = doubled(0) catch 99; // parse(0)=error.Zero -> try propagates -> caught -> 99
    _ = printf("ok=%d bad=%d\n", @as(c_int, ok), @as(c_int, bad));
    return ok;
}

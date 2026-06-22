// dotcc Zig front-end — comptime VALUES (Milestone T).
//
// `comptime EXPR` forces compile-time evaluation of a side-effect-free value and splices the
// result back into the program as a literal — including a CALL to a user function. dotcc
// interprets the callee's lowered body in a compile-time interpreter: recursion gets a fresh call
// frame, a `while` loop runs with local mutation, and an eval-step budget is the non-termination
// backstop (Zig's @setEvalBranchQuota). This evaluates VALUES only — a `comptime` that would
// produce a TYPE stays out of scope (the value-/type-comptime wall).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "fib10=55 fact5=120 sz=8")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A recursive comptime function — each call is a fresh interpreter frame.
fn fib(n: u32) u32 {
    if (n < 2) return n;
    return fib(n - 1) + fib(n - 2);
}

// A loop-based comptime function — a local `var`, a `while`, and mutation, all interpreted.
fn fact(n: u32) u32 {
    var r: u32 = 1;
    var i: u32 = 2;
    while (i <= n) { r = r * i; i = i + 1; }
    return r;
}

pub fn main() u8 {
    const f10: u32 = comptime fib(10);       // 55  — the recursion runs at compile time
    const f5: u32 = comptime fact(5);        // 120 — the loop runs at compile time
    const sz: u32 = comptime @sizeOf(u64);   // 8   — a comptime expression (no call)
    _ = printf("fib10=%u fact5=%u sz=%u\n", f10, f5, sz);
    return @intCast(f10 + f5 - sz - 125);    // 55 + 120 - 8 - 125 = 42
}

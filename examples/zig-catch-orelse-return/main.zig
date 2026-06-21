// dotcc Zig front-end — control-flow fallbacks: `catch return` / `orelse return` (Milestone N, part 6).
//
// `a catch return [v]` unwraps an error union's payload, or — on error — RETURNS from the current
// function (an early-out). `a orelse return [v]` is the same for an optional's none. These make
// `return` usable as the fallback (it's a statement, not a value), so dotcc lowers them structurally
// at a `const`/`var` initializer or a statement: hoist the operand, `if (error / none) { return …; }`,
// then bind the unwrapped payload on the success path. The `return` is wrapped correctly in a `!T`
// function (including `return error.X`). It is `try`-with-a-custom-fallback (use plain `try` for
// pure propagation).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-catch-orelse-return/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

fn mk(ok: bool) !i32 {
    if (ok) return 10;
    return error.Bad;
}

fn pick(present: bool) ?i32 {
    if (present) return 20;
    return null;
}

fn compute(a: bool, b: bool) !i32 {
    const x = mk(a) catch return error.NoX; // error union: on error, early-return a wrapped error
    const y = pick(b) orelse return 0; // value optional: on none, early-return 0
    return x + y;
}

pub fn main() u8 {
    var sum: i32 = 0;
    sum += compute(true, true) catch 99; // x=10, y=20 → 30
    sum += compute(false, true) catch 12; // mk errors → compute returns error.NoX → catch 12
    _ = printf("sum=%d\n", sum); // 30 + 12 = 42
    return @as(u8, @intCast(sum));
}

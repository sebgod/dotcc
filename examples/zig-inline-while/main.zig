// dotcc Zig front-end — `inline while` comptime loop UNROLLING — Milestone T, part 3.
//
// `inline while (cond) : (i = i + step) body` is a counted loop the compiler UNROLLS, driven by a
// `comptime var` counter. dotcc tracks the counter's value at lowering time: each round it folds the
// condition (with the counter substituted in), emits one body copy (the counter substituted to its
// current literal — so `arr[i]` becomes `arr[0]`, `arr[1]`, …), then folds the continue-expression to
// advance the counter. The loop and the counter vanish — no runtime `while`, no `i` variable.
//
// Because each copy is plain straight-line IR, the same construct works whether the enclosing function
// runs at runtime (the copies execute in order) or is itself `comptime`-called (the interpreter walks
// the unrolled copies). V1 unrolls the continue-expression form with a `comptime var` counter; a bare
// `inline while (c) body`, a runtime-`var` counter, or a `break`/`continue` in the body are clear
// deferred errors.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-inline-while/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "sum=50")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    const arr = [_]u32{ 5, 10, 15, 20 };
    var sum: u32 = 0;

    // The counter is a `comptime var`; the loop unrolls into `sum += arr[0]; sum += arr[1]; …`.
    comptime var i: usize = 0;
    inline while (i < 4) : (i = i + 1) {
        sum += arr[i];
    }

    _ = printf("sum=%u\n", sum); // 5 + 10 + 15 + 20 = 50
    return @intCast(sum - 8); // 50 - 8 = 42
}

// dotcc Zig front-end — while-story completion (capture-while `else` / `: (cont)` / error-union
// capture-while, and a non-zero for-slice index start).
//
// * `while (opt) |x| body else elsebody` — the `else` runs when the loop exits normally (the
//   optional yields null); a `break` in the body skips it, matching Zig.
// * `while (opt) |x| : (cont) body` — the continue-expression runs after each iteration AND on
//   `continue`, lowered to the C `for` post so the semantics match.
// * `while (eu) |x| body else |e| elsebody` — an error-union capture-while binds the success
//   payload each turn; on error it binds `e` (compared to `error.X`) and runs the else-branch.
// * `for (s, N..) |x, i|` — the index capture can start at a non-zero N.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-while-completion/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const E = error{ Stop };

fn nextLT(i: *i32, n: i32) ?i32 {
    if (i.* < n) {
        i.* += 1;
        return i.*;
    }
    return null;
}

fn step(i: *i32) E!i32 {
    if (i.* < 3) {
        i.* += 1;
        return i.*;
    }
    return error.Stop;
}

pub fn main() u8 {
    // capture-while with an `else` — the else runs on natural exit (optional null).
    var a: i32 = 0;
    var s1: i32 = 0;
    while (nextLT(&a, 3)) |v| {
        s1 += v; // 1 + 2 + 3 = 6
    } else {
        s1 += 100; // ran on exit → 106
    }

    // capture-while with a continue-expression (advances `cnt` each turn, incl. on `continue`).
    var b: i32 = 0;
    var cnt: i32 = 0;
    var s2: i32 = 0;
    while (nextLT(&b, 4)) |v| : (cnt = cnt + 1) {
        s2 += v; // s2 = 10, cnt = 4
    }

    // error-union capture-while — loops on the success payload, exits to `else |e|` on error.
    var c: i32 = 0;
    var s3: i32 = 0;
    var code: i32 = 0;
    while (step(&c)) |v| {
        s3 += v; // 1 + 2 + 3 = 6
    } else |e| {
        code = if (e == error.Stop) 9 else 1; // 9
    }

    // for-slice with a non-zero index start.
    const arr = [_]u8{ 10, 20, 30 };
    var acc: i32 = 0;
    for (arr[0..], 5..) |x, idx| {
        acc += @as(i32, x) + @as(i32, @intCast(idx)); // (10+5)+(20+6)+(30+7) = 78
    }

    _ = printf("s1=%d s2=%d cnt=%d s3=%d code=%d acc=%d\n", s1, s2, cnt, s3, code, acc);
    return 42;
}

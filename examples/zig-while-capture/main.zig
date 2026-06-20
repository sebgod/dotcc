// dotcc Zig front-end — optional capture-`while` (Milestone M, part 2).
//
// `while (opt) |x| { … }` re-evaluates the condition each iteration (it commonly advances an
// iterator); while the optional is non-null, it binds the payload to `x` and runs the body, and
// exits once the optional is null. dotcc desugars it to
//   while (true) { var __cap = cond; if (Cond.B(__cap.HasValue)) { var x = __cap.Value; … } else break; }
// — a real loop, so `break`/`continue` (and the labeled forms) compose. A `_` capture iterates
// without binding the payload.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-while-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// Pop 0, 1, 2, … advancing `*i`, until it reaches `max`; null when exhausted.
fn nextLT(i: *i32, max: i32) ?i32 {
    if (i.* >= max) return null;
    const v = i.*;
    i.* += 1;
    return v;
}

pub fn main() u8 {
    var sum: i32 = 0;

    // value optional capture-while — `v` is the bound payload each iteration.
    var i: i32 = 0;
    while (nextLT(&i, 9)) |v| {
        sum += v; // 0 + 1 + … + 8 = 36
    }

    // `_` discard capture-while — iterate without binding the payload.
    var j: i32 = 0;
    var count: i32 = 0;
    while (nextLT(&j, 6)) |_| {
        count += 1; // 6 iterations
    }
    sum += count; // +6

    _ = printf("sum=%d\n", sum); // 36 + 6 = 42
    return @as(u8, @intCast(sum));
}

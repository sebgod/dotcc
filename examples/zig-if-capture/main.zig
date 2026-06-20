// dotcc Zig front-end — optional payload capture in `if` (Milestone M, part 1).
//
// `if (opt) |x| { … } else { … }` binds the optional's payload to `x` in the then-branch (taken
// when the optional is non-null), and runs the else-branch otherwise. dotcc lowers a VALUE optional
// `?T` (C# `T?`) to `if (Cond.B(__cap.HasValue)) { var x = __cap.Value; … } else { … }`, and a niche
// optional POINTER `?*T` (a bare `T*`) to a non-null test with `x` bound to the pointer itself. A
// `_` capture tests without binding; the `else` is optional.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-if-capture/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// Returns the value when `present`, else `null` — a plain `?i32` producer.
fn pick(present: bool, val: i32) ?i32 {
    if (present) return val;
    return null;
}

pub fn main() u8 {
    var sum: i32 = 0;

    // value optional — payload bound in the then-branch.
    if (pick(true, 4)) |e| {
        sum += e; // +4
    } else {
        sum += 100;
    }

    // value optional — none → else.
    if (pick(false, 9)) |e| {
        sum += e;
    } else {
        sum += 10; // +10
    }

    // `_` discard — runs the then-branch when present, without binding the payload.
    if (pick(true, 999)) |_| {
        sum += 8; // +8
    }

    // no-else form — a none does nothing.
    if (pick(false, 5)) |e| {
        sum += e;
    }

    // optional POINTER capture — `p` is the unwrapped non-null pointer; write through it.
    var k: i32 = 0;
    const maybe: ?*i32 = &k;
    if (maybe) |p| {
        p.* = 20;
    }
    sum += k; // +20

    _ = printf("sum=%d\n", sum); // 4 + 10 + 8 + 20 = 42
    return @as(u8, @intCast(sum));
}

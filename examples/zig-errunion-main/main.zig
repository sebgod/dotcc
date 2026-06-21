// dotcc Zig front-end — error-union `main` (`pub fn main() !void` / `!u8`) (Milestone N, part 4).
//
// `main` itself may return an error union. dotcc emits `main` as `ErrUnion<…>` and wires the process
// entry to map the result exactly like real zig: an error → exit 1 (the flat error code is reported
// to stderr, so stdout stays clean), success → exit 0 (a `!void` payload, which has no exit value)
// or the integer payload value (`!u8`). A `try` inside `main` propagates an error out to that entry
// boundary, where it becomes the non-zero exit.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-errunion-main/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A fallible step — succeeds with `v` when `ok`, else fails with `error.Bad`.
fn step(ok: bool, v: i32) !i32 {
    if (ok) return v;
    return error.Bad;
}

pub fn main() !u8 {
    // `try` unwraps the payload on success, or propagates the error out of `main` (→ exit 1).
    const a = try step(true, 20);
    const b = try step(true, 22);
    _ = printf("total=%d\n", a + b); // 42
    return @as(u8, @intCast(a + b)); // the payload IS the process exit code
}

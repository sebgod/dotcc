// dotcc Zig front-end — defer / errdefer (Milestone H).
//
// `defer Stmt` runs its cleanup on EVERY exit from the enclosing block (fall-through, return,
// break, continue, or a propagating error), in LIFO order. `errdefer Stmt` runs only when the
// block exits via an error. The idiom this unlocks: pair an allocation with `defer
// allocator.free(...)` so the buffer is released on every path — the cleanup the allocator
// milestone (F) was missing.
//
// dotcc lowers:
//   * defer    → C# `try { rest } finally { cleanup }`                      (fires on all exits)
//   * errdefer → C# `try { rest } catch (ZigErrorReturn) { cleanup; throw; }` (error exit only)
//   * a function with an errdefer routes its `return error.X` through a throw, so the error
//     propagates through the errdefer catch (a direct return wouldn't be observed by a C# catch).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-defer/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "x")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

const std = @import("std");

extern fn putchar(c: c_int) c_int;

// Allocate `n` bytes, fill them with `v`, sum them, then ALWAYS free: `defer a.free(buf)` releases
// the buffer whether we fall out the bottom or return early — and (with an errdefer present
// elsewhere) even if an error propagates through.
fn sumFilled(a: std.mem.Allocator, n: usize, v: u8) !u8 {
    const buf = try a.alloc(u8, n);
    defer a.free(buf);
    var i: usize = 0;
    while (i < n) : (i = i + 1) {
        buf[i] = v;
    }
    var acc: u8 = 0;
    i = 0;
    while (i < n) : (i = i + 1) {
        acc = acc + buf[i];
    }
    return acc;
}

// `errdefer` marks a step to UNDO only when a later step fails. step(true) errors out, so its
// errdefer fires (prints 'x' = 120) on the way out and the error propagates to the caller's catch.
fn step(fail: bool) !u8 {
    errdefer _ = putchar(120);
    if (fail) return error.Boom;
    return 1;
}

pub fn main() u8 {
    // A FixedBufferAllocator over a stack buffer, surfaced as an opaque std.mem.Allocator.
    var buffer: [64]u8 = undefined;
    var fba = std.heap.FixedBufferAllocator.init(&buffer);
    const a = fba.allocator();

    // 6 bytes of value 7 → 42. The buffer is freed by the `defer` before sumFilled returns.
    const total = sumFilled(a, 6, 7) catch 0;

    // step errors → its `errdefer` fires (prints 'x'); we recover here with `catch 0`.
    const s = step(1 == 1) catch 0;

    _ = putchar(10);
    return total + s; // 42 + 0 = 42
}

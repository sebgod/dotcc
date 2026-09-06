// dotcc Zig front-end — the synthetic `builtin` module — road-to-zig-std S3.
//
// `@import("builtin")` is not a file. Real zig GENERATES it per compilation to describe the target,
// and dotcc does the same: a small module written as Zig source at compile start and fed through the
// ordinary import path, so it parses, binds and resolves exactly like any other module.
//
// What makes it useful is that every read below is settled at COMPILE time. Look at the emitted C#
// and there is no `if` left in `main` at all — each conditional has already chosen its arm and the
// other one is simply gone:
//
//     dotnet run --project DotCC -c Release -- examples/zig-target-builtin/main.zig --emit=file -o out.cs
//
// That is not an optimization, it is the whole point. In real std these conditionals guard inline
// assembly, raw syscalls and per-architecture intrinsics; a compiler that lowered BOTH arms would
// drown in code the branch exists to avoid. `builtin.cpu.arch` alone is asked 172 times in the
// pinned std, and `builtin.os.tag` 92 more.
//
// dotcc's `builtin` is deliberately DUCK-TYPED — bare enum literals and anonymous struct literals
// carrying the fields std reads. Real zig's version is typed against `std.Target`, which would drag
// in ~37,000 lines of per-architecture CPU feature tables describing hardware dotcc does not target:
// the program it emits is C#.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-target-builtin/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

const builtin = @import("builtin");

extern fn printf(format: [*c]const u8, ...) c_int;

// The idiom this exists for: a function that exists in several forms, one per platform, where only
// the right one may be lowered. Written as a comptime `switch` over the target — the other prongs
// are never even looked at.
fn pathSeparator() u8 {
    return switch (builtin.os.tag) {
        .windows => '\\',
        else => '/',
    };
}

// The same shape as an `if` chain. `.avr` and `.msp430` are 8- and 16-bit microcontrollers dotcc
// will never run on, so this always takes the last arm — but a compiler that could not settle the
// question would still have to lower the other two.
fn wordBits() u32 {
    if (builtin.cpu.arch == .avr) {
        return 8;
    } else if (builtin.cpu.arch == .msp430) {
        return 16;
    } else {
        return 64;
    }
}

pub fn main() u8 {
    // A plain boolean the target carries. dotcc reports `link_libc = true` on purpose: it biases std
    // toward its libc-backed paths, which bottom out in `extern fn`s dotcc's C-shaped runtime
    // already implements, rather than toward raw syscalls it does not.
    const libc: u32 = if (builtin.link_libc) 1 else 0;

    // Nested descriptors chain: `target` carries the same `cpu` / `os` / `abi` one level down, and
    // 100 uses in the pinned std reach them that way.
    const gnuish: u32 = if (builtin.target.abi == .gnu or builtin.target.abi == .none) 1 else 0;

    // A value-position `if` is a `const` RHS here rather than a call argument — dotcc's grammar
    // admits the value form only in the slots it was introduced for, which does not include an
    // argument. That is a parser limit, unrelated to the folding this example is about.
    const threaded: u32 = if (builtin.single_threaded) 0 else 1;
    const testing: u32 = if (builtin.is_test) 1 else 0;

    _ = printf("sep=%c bits=%u libc=%u gnuish=%u\n", @as(c_int, pathSeparator()), wordBits(), libc, gnuish);
    _ = printf("threaded=%u test=%u\n", threaded, testing);

    // 64 - 1 - 1 - 20 = 42
    return @intCast(wordBits() - libc - gnuish - 20);
}

// dotcc Zig front-end — sentinel-terminated types `[*:0]T` / `[:0]T` (Milestone O, part 3).
//
// The C-string shape Zig uses for C interop. `[*:0]T` is a NUL-terminated many-item pointer
// (C's `char*`); `[:0]T` is a NUL-terminated slice (`.len` excludes the sentinel). dotcc lowers
// them to the SAME `T*` / `Slice<T>` as `[*]T` / `[]T` — the sentinel is a type-level annotation,
// not separately enforced. dotcc's string literals are already NUL-terminated, so a manual
// `while (p[n] != 0)` scan works (the auto-scan `p[0..]` on a sentinel pointer is a documented cut).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-sentinel/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// scan a NUL-terminated C string to its length (manual, no std) — `[*:0]const u8` indexes like
// any pointer, and the sentinel guarantees the scan terminates.
fn clen(p: [*:0]const u8) usize {
    var n: usize = 0;
    while (p[n] != 0) : (n = n + 1) {}
    return n;
}

pub fn main() u8 {
    const s: [:0]const u8 = "abcdefghijklmnopqrstu"; // sentinel slice, .len = 21 (excludes NUL)
    const p: [*:0]const u8 = s.ptr; // a slice's `.ptr` is a `[*:0]const u8`
    const total = s.len + clen(p); // 21 + 21 = 42
    _ = printf("len=%zu scanned=%zu\n", s.len, clen(p));
    return @as(u8, @intCast(total)); // 42
}

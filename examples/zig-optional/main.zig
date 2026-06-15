// dotcc Zig front-end — optionals (`?T`): `null`, `.?`, `orelse`.
//
// Milestone B1. An optional has two lowerings, picked by the payload:
//   - `?*T` (optional POINTER) → a bare nullable `T*` (Zig's own niche): `null` is the
//     null pointer, zero overhead. The common C-interop shape.
//   - `?T` over a value type → C# `Nullable<T>` (`T?`).
// In both, `null` is none, `x.?` unwraps (panics on none), and `x orelse d` defaults —
// the value form lowers straight to C#'s null-coalescing `??` (single-eval, lazy `d`).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-optional/main.zig --emit=file -o out.cs
//             dotnet run out.cs                          # -> "x=99 y=99 z=7"
//   real zig: zig build-exe main.zig -lc && ./main       # -> same
extern fn printf(format: [*c]const u8, ...) c_int;

// Return the pointed-to value if the optional pointer is present, else the fallback's.
fn orDefault(a: ?*u8, fallback: *u8) *u8 {
    return a orelse fallback;
}

pub fn main() u8 {
    var fb: u8 = 99;
    const present: ?*u8 = &fb;       // some
    const absent: ?*u8 = null;       // none

    const x: u8 = orDefault(present, &fb).*;   // present  -> 99
    const y: u8 = orDefault(absent, &fb).*;    // none -> fallback -> 99

    const maybe: ?c_int = null;
    const z: c_int = maybe orelse 7;           // none -> 7

    _ = printf("x=%d y=%d z=%d\n", @as(c_int, x), @as(c_int, y), z);
    return 0;
}

// Milestone R, part 4 — the FFI declaration surface: `export fn` + `extern "c"`.
//
// `export fn` forces a function to C-ABI external linkage in Zig; `pub export fn` also
// makes it module-visible. dotcc lowers both as ordinary functions (every non-static
// function is already export-eligible under `-shared`), so they behave like a normal
// `fn` in a console program and are callable locally.
//
// `extern "c" fn …;` carries the optional library/calling-convention string after
// `extern`. dotcc accepts it and lowers it like a plain `extern fn` (routed to its
// libc-shaped runtime by bare name).
//
// The same peeling extends to exported / public DATA: `export const`/`export var`,
// `pub const`/`pub var`, and `pub export const` each lower as an ordinary global (the
// modifier is a no-op in a console program; a `-shared` data export is a documented cut).

extern "c" fn printf(format: [*c]const u8, ...) c_int;

export const tag: u8 = 7; // an exported data constant
pub var used: u8 = 0; // a public mutable data global

export fn add(a: u8, b: u8) u8 {
    return a + b;
}

pub export fn mul(a: u8, b: u8) u8 {
    return a * b;
}

pub fn main() u8 {
    used = tag; // write the exported const into the public var
    const r = add(mul(20, 2), 2); // 20*2 = 40, + 2 = 42
    _ = printf("r=%d used=%d\n", @as(c_int, r), @as(c_int, used));
    return r;
}

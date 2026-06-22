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

extern "c" fn printf(format: [*c]const u8, ...) c_int;

export fn add(a: u8, b: u8) u8 {
    return a + b;
}

pub export fn mul(a: u8, b: u8) u8 {
    return a * b;
}

pub fn main() u8 {
    const r = add(mul(20, 2), 2); // 20*2 = 40, + 2 = 42
    _ = printf("r=%d\n", @as(c_int, r));
    return r;
}

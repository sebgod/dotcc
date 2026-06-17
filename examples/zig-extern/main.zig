// dotcc Zig front-end — extern-fn libc FFI demo (real output).
//
// Modern Zig has no `@cImport` (removed in 0.16+); a standalone file calls C by
// declaring an `extern fn` prototype and linking libc. dotcc routes such a call
// by its bare name to its own `Libc` runtime — the same path a C program's libc
// call takes — so the program PRINTS without any `@cImport`/build.zig.
//
//   dotcc:      dotnet run --project DotCC -c Release -- examples/zig-extern/main.zig --emit=file -o out.cs
//               dotnet run out.cs                       # -> prints "Hi"
//   real zig:   zig build-exe main.zig -lc && ./main    # -> prints "Hi"
//
// `_ = putchar(...)` is Zig's explicit discard of the non-void result. `main`
// returns u8 (the process exit code) because dotcc's shell wires `return main();`
// — a `void` main is a separate follow-up.
extern fn putchar(c: c_int) c_int;

pub fn main() u8 {
    _ = putchar(72);   // 'H'
    _ = putchar(105);  // 'i'
    _ = putchar(10);   // '\n'
    return 0;
}

// dotcc Zig front-end — variadic libc FFI: printf with a format string.
//
// Builds on the extern-fn demo (examples/zig-extern): besides a fixed-arity
// `extern fn`, dotcc now lowers the VARIADIC form `fn(fixed, ...)` and string
// literals. `printf`'s format parameter has Zig's C-pointer type `[*c]const u8`
// (== C's `const char*`); the `...` is the variadic pack. dotcc routes `printf`
// by its bare name through the same printf-family fluent builder a C program
// uses, so the `%d` conversions format the trailing arguments.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-printf/main.zig --emit=file -o out.cs
//             dotnet run out.cs                          # -> "Hi 42 from Zig, sum=105"
//   real zig: zig build-exe main.zig -lc && ./main       # -> same
//
// `_ = printf(...)` discards the int result; `main` returns u8 (the exit code).
// Each variadic argument is cast with `@as(c_int, …)`: an untyped literal has no
// fixed-size ABI type, so both real zig and dotcc reject a bare `printf("%d", 42)`.
extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    _ = printf("Hi %d from Zig, sum=%d\n", @as(c_int, 42), @as(c_int, 100 + 5));
    return 0;
}

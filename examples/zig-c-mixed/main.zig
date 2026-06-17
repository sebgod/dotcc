// dotcc — mixed-language whole program: a Zig main + a C helper, one invocation.
//
// A single `dotcc` call may take BOTH .c and .zig translation units. Both lower
// into one IR module (the C group builds it; the Zig group lowers in), so the
// program emits once and a call across the language boundary resolves like any
// other — every function becomes a `DotCcProgram` method called by bare name.
// The Zig side declares the C function it calls as an `extern fn` (and printf the
// same way, routed to dotcc's libc runtime).
//
//   dotcc:  dotnet run --project DotCC -c Release -- \
//             examples/zig-c-mixed/main.zig examples/zig-c-mixed/util.c --emit=file -o out.cs
//           dotnet run out.cs                  # -> "square(8) + 3 = 67"
extern fn printf(format: [*c]const u8, ...) c_int;
extern fn square(n: c_int) c_int;   // defined in util.c

pub fn main() void {
    _ = printf("square(8) + 3 = %d\n", square(8) + 3);
}

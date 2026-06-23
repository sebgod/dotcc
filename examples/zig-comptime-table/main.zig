// dotcc Zig front-end — comptime functions returning an ARRAY (a lookup table) — Milestone T,
// comptime aggregates.
//
// The classic comptime use case: compute a lookup table at COMPILE TIME and bake it into the
// program as a literal. `comptime fibTable()` interprets the callee — `var t: [10]u32 = undefined;`
// zero-fills the array, the fill loop runs (`t[i] = t[i-1] + t[i-2]` reads prior elements AND
// writes the current one, all at comptime), and the finished table re-materializes as a
// `stackalloc u32[]{ 0, 1, 1, 2, … }` at the use site — no `fibTable()` call survives at runtime.
//
// `fibTable()` itself returns a `[10]u32` BY VALUE, which dotcc lowers soundly (a heap-owned copy;
// Zig arrays are value types). Use the table at a LOCAL `const` — real zig rejects the `comptime`
// keyword on a container const (already comptime); a runtime `const T = fibTable();` global works
// too (no keyword). This evaluates VALUES only — a `comptime` producing a TYPE stays out of scope.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime-table/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "fib[9]=34 fib[6]=8")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// Build a Fibonacci lookup table at compile time: [0, 1, 1, 2, 3, 5, 8, 13, 21, 34].
fn fibTable() [10]u32 {
    var t: [10]u32 = undefined;
    t[0] = 0;
    t[1] = 1;
    var i: usize = 2;
    while (i < 10) {
        t[i] = t[i - 1] + t[i - 2];
        i = i + 1;
    }
    return t;
}

pub fn main() u8 {
    const fib = comptime fibTable(); // folded to a stackalloc'd 10-element table literal
    _ = printf("fib[9]=%u fib[6]=%u\n", fib[9], fib[6]);
    return @intCast(fib[9] + fib[3] + fib[5] + 1); // 34 + 2 + 5 + 1 = 42
}

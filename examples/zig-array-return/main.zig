// dotcc Zig front-end — array-by-value return (the Milestone K cut, made sound).
//
// Zig arrays are VALUE types, so `fn f() [N]T` returns a COPY of the array. dotcc lowers a
// `[N]T` array to a `T*`, so a naive `return t;` of a stackalloc'd array local would hand back
// a pointer into the callee's dead frame — a dangling pointer that reads garbage. Instead dotcc
// copies the N elements into a heap-owned buffer and returns that pointer, so the value outlives
// the call. The function is called TWICE here: each call gets its own independent, correct copy
// (with the old bug both would alias the same clobbered stack slot).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-array-return/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "a3=9 b3=9")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A fixed-array-by-value return: t = [0, 1, 4, 9], copied out to the caller.
fn squares() [4]u32 {
    var t: [4]u32 = undefined;
    var i: usize = 0;
    while (i < 4) {
        t[i] = @intCast(i * i);
        i = i + 1;
    }
    return t;
}

pub fn main() u8 {
    const a = squares(); // [0, 1, 4, 9]
    const b = squares(); // a second, independent copy — not an alias of `a`
    _ = printf("a3=%u b3=%u\n", a[3], b[3]);
    // (1 + 4 + 9) + (1 + 4 + 9) + 14 = 42
    const s: u32 = a[1] + a[2] + a[3] + b[1] + b[2] + b[3] + 14;
    return @intCast(s);
}

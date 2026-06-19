// dotcc Zig front-end — tuples + destructuring (Milestone G).
//
// A Zig tuple is an anonymous positional struct (`.{ a, b }`, type `struct { T1, T2 }`).
// dotcc lowers the RUNTIME subset onto C# `System.ValueTuple`:
//
//   * a tuple TYPE `struct { u8, u8 }` (return / param / var) → `System.ValueTuple<byte, byte>`;
//   * a positional literal `.{ a, b }` → `new System.ValueTuple<…>(a, b)` (sink-typed or inferred);
//   * `const a, const b = e;` destructures — single-eval into a temp, then `.Item1` / `.Item2`;
//   * `t[N]` (a literal index) reads the Nth element → `.ItemN+1`.
//
// The comptime flavour of tuples (type-valued fields, the `std.fmt` `.{…}` reflection idiom)
// stays out of scope — see ZIG-SUPPORT.md.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-tuple/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

// A function that returns two values as a tuple — the headline use of tuples.
fn minmax(a: u8, b: u8) struct { u8, u8 } {
    if (a < b) return .{ a, b };
    return .{ b, a };
}

// A tuple TYPE as a parameter, indexed inside (`t[0]` / `t[1]`): the span hi - lo.
fn span(t: struct { u8, u8 }) u8 {
    return t[1] - t[0];
}

pub fn main() u8 {
    // Multiple return + destructure: lo = 6, hi = 18.
    const lo, const hi = minmax(18, 6);

    // The same pair, fed to a tuple-typed parameter (indexed inside): 18 - 6 = 12.
    const width = span(.{ lo, hi });

    // An inline literal, destructured directly (its tuple type is inferred): a = 4, b = 2.
    const a, const b = .{ @as(u8, 4), @as(u8, 2) };

    // lo + hi + width + a + b = 6 + 18 + 12 + 4 + 2 = 42.
    return lo + hi + width + a + b;
}

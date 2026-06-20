// dotcc Zig front-end — switch as an expression (Milestone L, part 1).
//
// `switch (subject) { v => e, a, b => e, else => e }` in value position — a typed binding, a
// return, or any `RhsExpr` slot. Each prong YIELDS a value (a bare-expr body `v => e`); `else` is
// the default. dotcc lowers it to C#'s native switch EXPRESSION (`subject switch { … }`), the same
// structural trick that makes `if` an expression — a `RhsExpr` alternative kept out of the operator
// cascade. (Block-bodied prongs needing a labeled `break :blk v` come in a later L increment.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-switch-expr/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Color = enum(u8) { red, green, blue };

// A return-position switch expression over an enum (`.member` labels + an `else`).
fn rank(c: Color) u8 {
    return switch (c) {
        .red => 10,
        .green => 20,
        else => 12,
    };
}

pub fn main() u8 {
    const n: u8 = 2;

    // A switch expression at a typed decl sink, with a multi-value prong and an `else` default.
    const grade: u8 = switch (n) {
        0, 1 => 5,
        2 => 20,
        else => 0,
    };

    const g = rank(.green); // 20
    const b = rank(.blue);  // 12 (via else)

    _ = printf("grade=%d green=%d blue=%d\n",
        @as(c_int, grade), @as(c_int, g), @as(c_int, b));

    // 20 + 20 + 12 - 10 = 42.
    return grade + g + b - 10;
}

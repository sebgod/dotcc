// dotcc Zig front-end — value-position `if`/`switch` with BLOCK-BODIED branches (Milestone Y, part 1).
//
// Milestone L made `if`/`switch` value-yielding when every prong/branch is a single expression (it
// lowers to a C# ternary / switch-expression). But a multi-statement arm yields its value through a
// labeled value-block `blk: { …; break :blk v; }` — and a C# expression can't host statements. So at
// a full `const`/`var`/`return`/assignment RHS, dotcc lowers the WHOLE `switch`/`if` as a C# STATEMENT
// that fills a result temp: every branch assigns the temp, then the binding reads it. The all-simple
// case still uses the clean expression lowering; only a block-bodied branch takes the temp-fill path.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-value-switch/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

// A return-position switch expression mixing a block-bodied prong, a multi-value prong, and a
// block-bodied `else` — each arm yields the binding's value.
fn classify(n: i32) i32 {
    const label = switch (n) {
        0 => blk: {
            const hundred: i32 = 100;
            break :blk hundred + 1;
        },
        1, 2 => 20,
        else => blk: {
            var acc: i32 = 0;
            acc = acc + n;
            break :blk acc;
        },
    };
    return label;
}

pub fn main() u8 {
    var total: i32 = 0;
    total = total + classify(1); // the `1, 2` prong → 20
    total = total + classify(7); // the block-bodied `else` → 7

    // A value-position `if` whose then-branch is a labeled value-block.
    const pick = if (total > 10) blk: {
        const bonus: i32 = 15;
        break :blk bonus;
    } else 0;
    total = total + pick; // +15

    return @intCast(total); // 20 + 7 + 15 = 42
}

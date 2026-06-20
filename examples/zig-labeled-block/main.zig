// dotcc Zig front-end — labeled block as a value (Milestone L, part 2).
//
// `blk: { …; break :blk v; }` in value position — a typed binding, a `return`, or an assignment
// RHS. The block runs statements (locals, control flow) and YIELDS a value via `break :blk v`.
// dotcc lowers it with the roadmap's temp-fill: a result temp is declared, each `break :blk v`
// assigns it and jumps to the block's end label, and the surrounding statement reads the temp —
// the only way a *statement* form can produce a *value*. (An if/switch-expression arm or a
// sub-expression position comes in a later L increment; for now it's a full `=`/`return` RHS.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-labeled-block/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A return-position labeled block with an early `break :blk` from inside an `if`.
fn classify(n: i32) i32 {
    return blk: {
        if (n < 0) break :blk 100;
        break :blk n * 2;
    };
}

pub fn main() u8 {
    // A typed-decl labeled value-block with an intermediate local.
    const doubled: i32 = blk: {
        const half: i32 = 10;
        break :blk half * 2; // 20
    };

    // An inferred labeled value-block — the result type comes from the break value.
    const tag = blk: {
        const base: i32 = 3;
        break :blk base + 4; // 7 (i32)
    };

    // An assignment-position labeled value-block.
    var acc: i32 = 0;
    acc = blk: {
        const t = classify(-1); // 100
        break :blk t - 95; // 5
    };

    _ = printf("doubled=%d tag=%d acc=%d cls=%d\n",
        doubled, tag, acc, classify(5)); // 20 7 5 10

    // 20 + 7 + 5 + 10 = 42.
    return @as(u8, @intCast(doubled + tag + acc + classify(5)));
}

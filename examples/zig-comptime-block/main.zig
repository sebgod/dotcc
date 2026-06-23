// dotcc Zig front-end — `comptime { … }` block STATEMENT — Milestone T, part 3.
//
// A `comptime { … }` block runs at COMPILE TIME. dotcc executes its statements at lowering time —
// folding `comptime var`/`const` declarations, assignments to comptime vars, and comptime `while`
// loops — and emits NO runtime code. Its only effect is on comptime VALUES: an enclosing `comptime
// var` mutated inside the block keeps its computed value, and later references substitute that value
// as a literal. Here the block sums 1..8 into `total` (= 36) with a comptime `while`; at runtime there
// is no loop and no `total` variable — just the folded `36`.
//
// V1 runs a compile-time value subset (var/const decls, assignments to comptime vars, `while` loops).
// A store to a runtime `var` inside the block is an error (the block has no runtime effect), matching
// real zig; compile-time assertions (`@compileError`) and comptime-producing-a-type stay out of scope.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime-block/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "total=36")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    comptime var total: u32 = 0;

    // The whole block runs at comptime; `total` is computed to 36 and baked in.
    comptime {
        var i: u32 = 1;
        while (i <= 8) : (i = i + 1) {
            total = total + i;
        }
    }

    _ = printf("total=%u\n", @as(u32, total)); // 1 + 2 + … + 8 = 36
    return @intCast(total + 6); // 36 + 6 = 42
}

// dotcc Zig front-end — labeled loops + labeled break/continue (Milestone L, part 3).
//
// A loop may carry a `label:`; `break :label` exits it and `continue :label` next-iterates it —
// and the target may be an OUTER loop, the whole point of the feature. C# has no labeled
// break/continue, so dotcc lowers them to `goto`: `break :lbl` jumps to a label just AFTER the
// loop, `continue :lbl` jumps to a label at the END of the loop body (so the loop's natural
// iteration step — here the `: (i += 1)` continue-expression — still runs). The labels are
// emitted only when actually used.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-labeled-loop/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

pub fn main() u8 {
    // `break :outer` from a nested loop — find the first (i, j) with i*j >= 6.
    var found_i: i32 = 0;
    var found_j: i32 = 0;
    var i: i32 = 1;
    outer: while (i <= 5) : (i = i + 1) {
        var j: i32 = 1;
        while (j <= 5) : (j = j + 1) {
            if (i * j >= 6) {
                found_i = i;
                found_j = j;
                break :outer; // exits BOTH loops
            }
        }
    }
    // i=2, j=3 → 6 is the first hit: found_i=2, found_j=3.

    // `continue :outer` from a nested loop — count (a, b) pairs but skip the rest of the inner
    // loop AND the trailing `hits += 100` once b reaches 1, for every a.
    var count: i32 = 0;
    var hits: i32 = 0;
    var a: i32 = 0;
    scan: while (a < 3) : (a = a + 1) {
        var b: i32 = 0;
        while (b < 3) : (b = b + 1) {
            count += 1;
            if (b == 1) continue :scan; // skip b=2 and the `hits` line below
        }
        hits += 100; // never reached — `continue :scan` always jumps over it
    }
    // a in {0,1,2}, each counting b=0 and b=1 → count = 6; hits stays 0.

    _ = printf("fi=%d fj=%d count=%d hits=%d\n", found_i, found_j, count, hits); // 2 3 6 0

    // count*count (36) + found_i*found_j (6) = 42.
    return @as(u8, @intCast(count * count + found_i * found_j));
}

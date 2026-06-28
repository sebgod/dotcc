// dotcc Zig front-end — value-position LOOPS `while/for (…) … else …` (Milestone Y, part 2).
//
// A `while`/`for` with an `else` clause is an EXPRESSION: it yields a `break v` value (on break), or
// the `else` value on normal completion — the idiomatic search loop. dotcc lowers it as a C#
// STATEMENT filling a result temp `__lv`: a `break v` becomes `__lv = v; goto __lv_end;` (skipping
// the `else`), and the `else` value is assigned after the loop on natural completion. A labeled value
// loop (`lbl: … else …`) lets `break :lbl v` yield from an OUTER loop, the goto jumping out of every
// enclosing loop at once. (The for-RANGE / indexed `|x, i|` / capture / continue-expr value forms are
// V1 cuts.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-value-loop/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

// A `while … else` value loop with an unlabeled `break v`.
fn whileVal() i32 {
    var i: i32 = 0;
    return while (i < 50) {
        i = i + 1;
        if (i == 20) break i; // -> 20
    } else 0;
}

// A LABELED `while … else` value loop: `break :outer v` yields from the outer loop, out of the
// nested (statement) loop in one jump.
fn labeledWhileVal() i32 {
    var i: i32 = 0;
    return outer: while (i < 5) {
        var j: i32 = 0;
        while (j < 5) {
            if (i == 2 and j == 3) break :outer 15; // -> 15
            j = j + 1;
        }
        i = i + 1;
    } else 0;
}

// A `for (slice) |x| … else …` value loop — the search idiom.
fn forVal(xs: []const i32) i32 {
    return for (xs) |x| {
        if (x > 5) break x; // first element > 5 -> 7
    } else 0;
}

pub fn main() u8 {
    var arr = [_]i32{ 1, 2, 7, 9 };
    var total: i32 = 0;
    total = total + whileVal(); // 20
    total = total + labeledWhileVal(); // 15
    total = total + forVal(arr[0..]); // 7
    return @intCast(total); // 20 + 15 + 7 = 42
}

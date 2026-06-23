// dotcc Zig front-end — `@alignOf` / `@offsetOf` as comptime values — Milestone T, part 4.
//
// `@sizeOf(T)`, `@alignOf(T)`, and `@offsetOf(T, "field")` are all compile-time-known integers that
// dotcc computes from its layout model (the same one C's `sizeof`/`offsetof` use). `@alignOf` folds
// straight to a literal; `@offsetOf` reuses the C `offsetof` IR, so it both folds in a comptime-
// required position (an array bound `[@offsetOf(T, "m")]u8`) and renders a layout computation at a
// runtime use site. The result IS a comptime value, so it participates in comptime arithmetic.
//
// An `extern struct` pins the C-ABI field layout (a plain Zig `struct` is free to reorder fields, so
// its offsets need not match C) — so dotcc and real zig agree exactly:
//   Point { a: u8, b: u32, c: u16 }  ->  size 12, align 4, b @ offset 4, c @ offset 8.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-align-offsetof/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "sz=12 al=4 ob=4 oc=8")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Point = extern struct {
    a: u8,
    b: u32,
    c: u16,
};

pub fn main() u8 {
    const sz: usize = @sizeOf(Point); // 12
    const al: usize = @alignOf(Point); // 4
    const ob: usize = @offsetOf(Point, "b"); // 4
    const oc: usize = @offsetOf(Point, "c"); // 8
    _ = printf("sz=%zu al=%zu ob=%zu oc=%zu\n", sz, al, ob, oc);
    return @intCast(sz + al + ob + oc + 14); // 12 + 4 + 4 + 8 + 14 = 42
}

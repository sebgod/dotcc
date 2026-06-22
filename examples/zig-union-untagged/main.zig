// Milestone R, part 3 — untagged `union { … }`.
//
// An untagged union has no discriminant: all variants share one storage location.
// dotcc lowers it to a bare overlapping-payload struct ([StructLayout(Explicit)],
// every field at offset 0) — NO outer __tag/__payload wrapper. Construction and
// field access use the ordinary struct paths.
//
// V1 note: Zig's safe-mode active-field tracking / type-pun checks are NOT modeled.
// Same-field read/write (write a field, then read THAT field) is faithful; reading
// a field other than the last one written (punning) is unmodeled — so this example
// keeps each union value to a single active field.

const Box = union {
    small: u8,
    big: u32,
};

pub fn main() u8 {
    var a: Box = .{ .small = 10 };
    a.small += 5; // 15 (same-field)

    var b: Box = .{ .big = 25 };
    b.big += 2; // 27 (same-field)

    // 15 + 27 = 42
    return @intCast(@as(u32, a.small) + b.big);
}

// dotcc Zig front-end — REIFICATION builtins + `@compileError` — road-to-zig-std S7.
//
// The direction opposite to `@typeInfo`. S5 reads a type's description OUT (`@typeInfo(T).int.bits`);
// `@Int(signedness, bits)` builds a type back IN from one, and `@compileError` is how a comptime
// program rejects a type it was handed. Together they are what a reflective std function is made of:
// look at T, decide, and either construct the type you need or refuse in the author's own words.
//
// Two things here are worth watching, because they are where a transpiler usually goes quietly wrong:
//
//   - `@bitSizeOf` reports the DECLARED width, not the storage width. dotcc has no sub-word integer,
//     so `u21` lowers to a 32-bit `uint` — and still answers 21, because the width rides the binding
//     (road-to-zig-std S5b) rather than being read back off the lowered type. `@Int(.unsigned, 21)`
//     joins that same road: it records the width it was built with, so `Rebuilt` below reports 9.
//
//   - `@compileError` fires where it is REACHED, not where it is written. Every diagnostic in this
//     file sits in a branch that comptime folding removes — the `else` prong of a `switch` over
//     `@typeInfo`, an `if` on a comptime-known condition, and a top-level deprecation tombstone that
//     nothing names. Real zig does exactly the same (its reference: the error is raised "when
//     semantically analyzed", and `if`/`switch` on compile-time constants is one of the several ways
//     code avoids being checked), which is why this program compiles at all. If any of the three
//     fired, neither compiler would produce a binary.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-reify/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A DEPRECATION TOMBSTONE — the shape real std uses 25 times (std/meta.zig, std/os/windows.zig, …).
// It is a declaration whose value is a compile error, so naming it fails the build with this message
// while merely importing the file does not. Nothing below names it, so nothing happens.
pub const OLD_MASK_TYPE = @compileError("use MaskOf(T) instead");

// Types that no keyword spells: 21 unsigned bits, 9 signed bits.
const Word21 = @Int(.unsigned, 21);
const Small = @Int(.signed, 9);

fn widthOf(comptime T: type) i32 {
    return @bitSizeOf(T);
}

// The guard idiom: dispatch on the kind, and make every OTHER kind a compile error naming itself.
// Only the `.int` prong is ever lowered, so the `else` is never analysed.
fn onlyInts(comptime T: type) i32 {
    return switch (@typeInfo(T)) {
        .int => @bitSizeOf(T),
        else => @compileError("onlyInts wants an integer, got " ++ @typeName(T)),
    };
}

// The other guard shape — a comptime-known `if`. `@bitSizeOf(T) > 64` settles at compile time, so the
// `@compileError` is dead code for every T this program passes.
fn narrow(comptime T: type) i32 {
    if (@bitSizeOf(T) > 64) @compileError("narrow() is for types that fit a machine word");
    return @bitSizeOf(T);
}

pub fn main() u8 {
    // Raise the comptime evaluation budget. Zig counts backward branches, dotcc counts eval steps —
    // a finer unit — and both treat the quota as a floor that is never lowered, so this is honored
    // in both compilers and changes nothing observable in either.
    @setEvalBranchQuota(10000);

    var w: Word21 = 2000;
    var s: Small = -200;
    w += 0;
    s += 0;
    _ = printf("w=%d s=%d\n", @as(c_int, w), @as(c_int, s));

    // The width survives the trip through a `comptime T: type` parameter.
    _ = printf("bits: Word21=%d Small=%d\n", widthOf(Word21), widthOf(Small));
    _ = printf("bits: bool=%d usize=%d enum16=%d\n",
        @as(c_int, @bitSizeOf(bool)), @as(c_int, @bitSizeOf(usize)), @as(c_int, @bitSizeOf(Tag)));

    // Take a type apart and put it back together: signedness out of `@typeInfo`, width out of
    // `@bitSizeOf`, both back into `@Int`. The result is `Small` again, and still says so.
    const Rebuilt = @Int(@typeInfo(Small).int.signedness, @bitSizeOf(Small));
    var r: Rebuilt = -200;
    r += 0;
    _ = printf("rebuilt: bits=%d value=%d\n", widthOf(Rebuilt), @as(c_int, r));

    _ = printf("guarded: onlyInts(u7)=%d narrow(u14)=%d\n", onlyInts(u7), narrow(u14));

    // 21 + 9 + 1 + 11 = 42
    return @intCast(widthOf(Word21) + widthOf(Small) + @bitSizeOf(bool) + 11);
}

const Tag = enum(u16) { a, b };

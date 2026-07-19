// dotcc Zig front-end — a comptime OPTIONAL value parameter that folds a captured `if`.
//
// A `comptime opt: ?T` parameter makes the function a template (like any comptime-value param).
// A captured `if (opt) |x| … else …` on that parameter FOLDS at instantiation time: a payload
// argument selects the then-branch (binding `x` to the compile-time value), a `null` argument
// selects the else-branch — no runtime optional test survives in the specialized body. Each
// distinct argument monomorphizes its own instance (`choose__opt41`, `choose__optnull`).
//
// This is the user-generic analog of how `std.ArrayList`'s `Aligned(T, alignment)` selects its
// `Slice` type from a `comptime alignment: ?mem.Alignment` — the S4 arc toward compiling
// std.ArrayList from source. (Here the folded branches are VALUES; folding them to a TYPE is the
// next step of that arc.)
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime-optional/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

/// A payload argument folds to `x + 1`; a `null` argument folds to `0`.
fn choose(comptime opt: ?u8) u8 {
    return if (opt) |x| x + 1 else 0;
}

/// The same fold works in statement position inside the instance body.
fn labelOr(comptime tag: ?u8, dflt: u8) u8 {
    var r: u8 = dflt;
    if (tag) |t| {
        r = t;
    } else {
        r = dflt;
    }
    return r;
}

pub fn main() u8 {
    const a = choose(19); // some(19) -> 20
    const b = choose(null); // null    -> 0
    const c = labelOr(22, 0); // some(22) -> 22
    const d = labelOr(null, 0); // null    -> 0
    return a + b + c + d; // 20 + 0 + 22 + 0 = 42
}

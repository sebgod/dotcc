// dotcc Zig front-end — a captured `if` in VALUE position.
//
// `if (opt) |x| thenExpr else elseExpr` is Zig's optional-unwrap-with-default as an EXPRESSION:
// when `opt` has a value it binds `x` and yields `thenExpr` (which may use `x`); otherwise it
// yields `elseExpr`. Because it binds `x`, dotcc can't lower it to a bare C# ternary — it hoists
// to a result temp assigned by a real `if` whose then-branch binds `x = opt.Value` (or the
// unwrapped pointer). `else` is mandatory: an expression must yield a value on both paths.
//
// This is the value-position sibling of the statement `if (opt) |x| { … }` form, and the parse
// gate the comptime-type computation in `std.ArrayList`'s `Aligned(T, alignment)` needs
// (`const Slice = if (alignment) |a| …align(a)… else []T;`).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-if-capture-expr/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

/// Optional payload + default, as an expression.
fn plusOneOrZero(opt: ?u8) u8 {
    return if (opt) |x| x + 1 else 0;
}

/// Optional-POINTER capture in value position: the unwrapped pointer is dereferenced.
fn derefOr(p: ?*const u8, dflt: u8) u8 {
    return if (p) |q| q.* else dflt;
}

pub fn main() u8 {
    const has = plusOneOrZero(19); // 20
    const none = plusOneOrZero(null); // 0
    const n: u8 = 22;
    const viaPtr = derefOr(&n, 0); // 22
    const noPtr = derefOr(null, 0); // 0
    return has + none + viaPtr + noPtr; // 20 + 0 + 22 + 0 = 42
}

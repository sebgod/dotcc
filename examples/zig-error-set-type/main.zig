// dotcc Zig front-end — an error set as a plain VALUE type (Milestone X, part 3b).
//
// Earlier milestones used a named error set ONLY as the `E` in an `E!T` return type. Zig also lets
// an error set name stand as an ordinary value type — a parameter, a `var`/`const`, or a non-`!T`
// return — denoting the error VALUE itself (`fn worst() MathError` returns an error, not a
// `MathError!T` union). dotcc keeps the V1 flat erasure: such a type lowers to the same `ushort`
// error code as `error.X`, so error values pass, compare, and `switch` exactly as before.
//
// It also shows the exhaustive error `switch` EXPRESSION with NO `else`: real zig proves the prongs
// cover every member, so an `else` is neither required nor allowed. dotcc can't prove coverage over
// the erased `ushort`, so it injects the implicit `_` default arm (the last prong) — keeping the
// emit warning-clean while staying semantics-preserving for the correct, exhaustive program.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-error-set-type/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

const MathError = error{ DivByZero, Overflow, Underflow };

// An error set as a plain RETURN type (not `E!T`) — returns the error VALUE itself.
fn worst() MathError {
    return MathError.Overflow;
}

// An error set as a PARAMETER type + an EXHAUSTIVE switch EXPRESSION with NO `else`.
fn weight(e: MathError) u8 {
    return switch (e) {
        error.DivByZero => 10,
        error.Overflow => 20,
        error.Underflow => 5,
    };
}

pub fn main() u8 {
    const e: MathError = worst(); // error-typed local from an error-returning fn
    var acc: u8 = weight(e); // 20 (Overflow)
    acc += weight(MathError.DivByZero); // +10 (set-qualified member)
    acc += weight(error.Underflow); // +5  (bare form, same code)
    acc += 7;
    return acc; // 20 + 10 + 5 + 7 = 42
}

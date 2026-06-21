// dotcc Zig front-end — explicit `error{A, B}` set declarations + named `E!T` (Milestone N, part 5).
//
// Zig programs declare named error sets — `const FileError = error{ NotFound, Permission };` — and
// use them as `E!T` return types. dotcc ERASES the set into the flat global error-code space (the
// model from parts 1–4): `const E = error{…};` is a COMPTIME declaration that registers the member
// names (assigning each a stable code) and emits NO runtime decl; `E` is then used only as the
// (ignored) set in an `E!T` return type, which lowers to the same `ErrUnion<T>` as `anyerror!T`.
// `error.X` members and `catch` work exactly as in the flat-code model.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-error-set/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// A named error set + a function declared to return `MathError!i32`.
const MathError = error{ Overflow, Negative };

fn checked(n: i32) MathError!i32 {
    if (n > 100) return error.Overflow;
    if (n < 0) return error.Negative;
    return n * 2;
}

pub fn main() u8 {
    var sum: i32 = 0;
    sum += checked(5) catch 0; // ok → 10
    sum += checked(-1) catch 12; // error.Negative → catch 12
    sum += checked(200) catch 20; // error.Overflow → catch 20
    _ = printf("sum=%d\n", sum); // 10 + 12 + 20 = 42
    return @as(u8, @intCast(sum));
}

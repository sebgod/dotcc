// dotcc Zig front-end — compound assignment operators.
//
// Zig has the ten `x op= y` operators (`+= -= *= /= %= <<= >>= &= |= ^=`) but deliberately
// NO `++`/`--`: the idiom for increment is `i += 1`. Each lowers to a C# compound assignment,
// so the target lvalue is evaluated exactly ONCE — `arr[next()] += 1` calls `next()` a single
// time, not twice (which a naive `arr[next()] = arr[next()] + 1` rewrite would).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-compound-assign/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "sum=42")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// Sum 1..=n with `+=` as the accumulator and `+= 1` as the loop step (Zig's `++` replacement).
fn triangle(n: u8) u8 {
    var sum: u8 = 0;
    var i: u8 = 1;
    while (i <= n) {
        sum += i;
        i += 1;
    }
    return sum;
}

pub fn main() u8 {
    var x = triangle(8); // 1+2+…+8 = 36
    x += 10; // 46
    x -= 4; // 42
    _ = printf("sum=%d\n", @as(c_int, x));
    return x;
}

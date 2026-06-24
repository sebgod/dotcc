// Milestone X, part 2 — `E.member`: a set-qualified error reference.
//
// Zig lets you name an error either bare (`error.Boom`) or qualified by a declared set
// (`MyError.Boom`). dotcc erases set MEMBERSHIP to one flat code, so `MyError.Boom` resolves to the
// SAME code as `error.Boom` (real zig: the same global error value) — usable as a value, in a
// comparison, and in `return` position.
//
// acc: Boom==Boom (+20), Fizz==Fizz (+20), Boom!=Fizz (+1); boom() returns MyError.Boom, caught → 1.
// 20 + 20 + 1 + 1 = 42.

const MyError = error{ Boom, Fizz };

fn boom() MyError!u8 {
    return MyError.Boom; // E.member in return position
}

pub fn main() u8 {
    var acc: u8 = 0;
    if (MyError.Boom == error.Boom) acc += 20; // set-qualified == bare (Boom)
    if (MyError.Fizz == error.Fizz) acc += 20; // set-qualified == bare (Fizz)
    if (MyError.Boom != MyError.Fizz) acc += 1; // distinct members
    const r = boom() catch 1; // boom() errors → caught → 1
    return acc + r; // 20 + 20 + 1 + 1 = 42
}

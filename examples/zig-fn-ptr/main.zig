// Milestone W, part 1a — function-pointer types + `anyopaque`.
//
// Two operations share one signature `fn (ctx: *anyopaque, by: i32) i32`: each treats its
// opaque context as a `*i32` accumulator (the C `void*`-callback idiom), mutates it, and
// returns the new value. `main` binds each function to a `*const fn (…) i32` value and calls
// it INDIRECTLY through that pointer — the whole point of the increment.
//
// A fn-pointer type's params may be UNNAMED (`fn (i32, i32) i32`, the common Zig form) as well as
// named, and the return may be an error union (`fn (i32) E!i32`).
//
// acc: 5 --bump 16--> 21 --scale 2--> 42  →  exit 42.

const E = error{ Bad };

// A fn-pointer GLOBAL that forward-references a function declared LATER — typed and inferred.
// (Functions are registered before globals, so the forward reference resolves.)
const adder: *const fn (i32, i32) i32 = &add;
const adder2 = &add; // inferred fn-ptr global (`&fn` is a callable `CType.Func` value)

fn bump(ctx: *anyopaque, by: i32) i32 {
    const p: *i32 = @ptrCast(@alignCast(ctx));
    p.* = p.* + by;
    return p.*;
}

fn scale(ctx: *anyopaque, by: i32) i32 {
    const p: *i32 = @ptrCast(@alignCast(ctx));
    p.* = p.* * by;
    return p.*;
}

fn add(a: i32, b: i32) i32 {
    return a + b;
}

fn dbl(x: i32) E!i32 {
    if (x < 0) return error.Bad;
    return x * 2;
}

pub fn main() u8 {
    var acc: i32 = 5;
    const f1: *const fn (ctx: *anyopaque, by: i32) i32 = bump;
    const f2: *const fn (ctx: *anyopaque, by: i32) i32 = scale;
    _ = f1(&acc, 16); // acc = 5 + 16 = 21
    _ = f2(&acc, 2); //  acc = 21 * 2 = 42

    // Unnamed-param and error-returning fn-pointer TYPES.
    const g: *const fn (i32, i32) i32 = add; // UNNAMED params
    const h: *const fn (i32) E!i32 = dbl; //    !T-returning
    if (g(1, 2) != 3) return 0; // sanity — leave `acc` (42) as the exit unless wrong
    if ((h(3) catch 0) != 6) return 0;
    if (adder(1, 2) != adder2(1, 2)) return 0; // both fn-ptr globals call the same `add`

    return @intCast(acc);
}

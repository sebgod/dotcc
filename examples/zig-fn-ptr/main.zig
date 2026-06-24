// Milestone W, part 1a — function-pointer types + `anyopaque`.
//
// Two operations share one signature `fn (ctx: *anyopaque, by: i32) i32`: each treats its
// opaque context as a `*i32` accumulator (the C `void*`-callback idiom), mutates it, and
// returns the new value. `main` binds each function to a `*const fn (…) i32` value and calls
// it INDIRECTLY through that pointer — the whole point of the increment.
//
// acc: 5 --bump 16--> 21 --scale 2--> 42  →  exit 42.

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

pub fn main() u8 {
    var acc: i32 = 5;
    const f1: *const fn (ctx: *anyopaque, by: i32) i32 = bump;
    const f2: *const fn (ctx: *anyopaque, by: i32) i32 = scale;
    _ = f1(&acc, 16); // acc = 5 + 16 = 21
    _ = f2(&acc, 2); //  acc = 21 * 2 = 42
    return @intCast(acc);
}

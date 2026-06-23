// dotcc Zig front-end — comptime functions returning a STRUCT value (Milestone T, comptime
// aggregates).
//
// `comptime mk(...)` interprets the callee at compile time and splices the RESULT back into the
// program. When the callee returns a struct, dotcc's compile-time interpreter holds the struct as
// a field map: `var v: V = undefined;` zero-fills it, each `v.field = ...;` mutates it, and the
// arithmetic folds — then the value re-materializes as a `new V { ... }` object initializer at the
// use site (so no `mk` call survives there). This evaluates VALUES only — a `comptime` producing a
// TYPE stays out of scope (the value-/type-comptime wall).
//
// Both uses are LOCAL `const x = comptime ...;` — the round-trippable form: real zig rejects the
// `comptime` keyword on a CONTAINER-level const ("redundant comptime in already-comptime scope",
// since a container const is itself a comptime context).
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-comptime-struct/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "g=21 l=10")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const V = struct { a: u32, b: u32, sum: u32 };

// A struct-returning function, interpreted at compile time when called under `comptime`.
fn mk(x: u32, y: u32) V {
    var v: V = undefined;
    v.a = x;
    v.b = y;
    v.sum = v.a + v.b;
    return v;
}

pub fn main() u8 {
    const g = comptime mk(10, 11); // folded to `new V { a = 10, b = 11, sum = 21 }`
    const l = comptime mk(4, 6); //   folded to `new V { a = 4, b = 6, sum = 10 }`
    _ = printf("g=%u l=%u\n", g.sum, l.sum);
    return @intCast(g.sum + l.sum + 11); // 21 + 10 + 11 = 42
}

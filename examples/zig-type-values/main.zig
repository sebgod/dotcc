// Type-as-value foundation (wall-plan W1) — Zig's "types are values", so a `const`
// can bind a NAME to a TYPE and `@TypeOf(expr)` yields an expression's type. dotcc
// records each such binding as a lowering-time type alias and resolves it in any
// type position; the composed prefixes (`*T`, `?T`, `[]T`) ride over the aliased
// element for free. No runtime value is emitted for an alias, exactly like real zig.
//
// Run:   dotnet run --project DotCC -c Release -- examples/zig-type-values/main.zig
// Zig:   zig run main.zig -lc     (same output — the differential oracle's claim)
extern fn printf(fmt: [*:0]const u8, ...) c_int;

// A bare type alias — a name bound to a type.
const Elem = i32;

fn addElems(a: Elem, b: Elem) Elem {
    return a + b;
}

// A pointer-prefix type composed over ANOTHER alias.
const ElemPtr = *Elem;

fn bump(p: ElemPtr) void {
    p.* = p.* + 1;
}

pub fn main() void {
    const x: Elem = 20;
    // @TypeOf in a type position — y has exactly x's type.
    const y: @TypeOf(x) = 22;
    _ = printf("sum=%d\n", addElems(x, y));

    // A local alias via @TypeOf, then a mutable local of that type.
    const T = @TypeOf(x);
    var acc: T = 0;
    acc = acc + x;
    acc = acc + y;
    bump(&acc);
    _ = printf("acc=%d\n", acc);

    // An optional composed over the alias.
    var maybe: ?Elem = null;
    maybe = 7;
    if (maybe) |v| {
        _ = printf("maybe=%d\n", v);
    }
}

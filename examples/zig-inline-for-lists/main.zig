// `inline for` over a comptime list — road-to-zig-std S6.
//
// S5 taught dotcc to ANSWER questions about a type; this is the brick that lets code loop over the
// answers. A `@typeInfo` member list is a comptime value with no runtime representation, so a plain
// `for` cannot walk one — there is nothing there to iterate. `inline for` can, because it is not a
// loop at all: it is UNROLLED at lowering time into one copy of the body per element, with the
// capture bound as a comptime binding rather than a runtime variable.
//
// That is what makes the capture usable where a variable could never go. A `field_types` capture
// binds a TYPE, so `@sizeOf(F)` resolves per copy. A `field_names` capture binds a comptime STRING,
// so `@field(v, name)` becomes an ordinary field access — which is how one generic function reaches
// every field of a struct it has never seen, with no reflection at runtime and nothing left in the
// emitted C# but the field accesses themselves.
//
// Four operand shapes are unrolled, all measured against the pinned std rather than guessed at:
// a single list, two lists walked in PARALLEL (`|name, F|` — the commonest of the four), a list
// alongside its indices (`, 0..`), and a `[_]type{…}` literal.
//
// NOTE: the member-list lines use zig 0.17-dev's `field_names` / `field_types`, which replaced
// 0.16's `fields: []const StructField`. dotcc targets 0.17-dev (the version whose std it compiles),
// so building this with real zig needs 0.17-dev too. The `[_]type{…}` lines work on either.
//
//   dotnet run --project DotCC -c Release -- --emit=file examples/zig-inline-for-lists/main.zig > out.cs
//   dotnet out.cs   # prints the eight lines below, exits 42
//
// Output:
//   field   x is 4 byte(s)
//   field   y is 4 byte(s)
//   field   z is 4 byte(s)
//   sum     Point fields add to 42
//   widths  Point is 12 bytes across 3 fields
//   type    #0 is 1 byte(s)
//   type    #1 is 2 byte(s)
//   type    #2 is 4 byte(s)

extern fn printf(fmt: [*:0]const u8, ...) c_int;

const Point = struct { x: i32, y: i32, z: i32 };

/// Add up every field of ANY struct. The loop is written once and unrolls per field of whatever
/// `T` turns out to be — `@field(v, name)` is an ordinary `v.x` / `v.y` / `v.z` by the time it
/// reaches the IR, so nothing about this is dynamic.
fn sumFields(comptime T: type, v: T) i32 {
    var total: i32 = 0;
    inline for (@typeInfo(T).@"struct".field_names) |name| {
        total += @field(v, name);
    }
    return total;
}

/// The same walk over `field_types` instead — the capture is a TYPE, which no runtime variable
/// could hold, so `@sizeOf(F)` is answered per copy at lowering time.
fn sizeOfFields(comptime T: type) i32 {
    var total: i32 = 0;
    inline for (@typeInfo(T).@"struct".field_types) |F| {
        const w: i32 = @sizeOf(F);
        total += w;
    }
    return total;
}

pub fn main() u8 {
    const p = Point{ .x = 20, .y = 14, .z = 8 };

    // The PARALLEL form: `field_names` and `field_types` are index-parallel, so one loop binds a
    // name and a type together. Each iteration is a separate `printf` call in the emitted C#.
    inline for (@typeInfo(Point).@"struct".field_names, @typeInfo(Point).@"struct".field_types) |name, F| {
        const w: i32 = @sizeOf(F);
        _ = printf("field   %c is %d byte(s)\n", name[0], w);
    }

    _ = printf("sum     Point fields add to %d\n", sumFields(Point, p));

    const nf: i32 = @typeInfo(Point).@"struct".field_names.len;
    _ = printf("widths  Point is %d bytes across %d fields\n", sizeOfFields(Point), nf);

    // A `[_]type{…}` literal walked alongside its own indices. Neither operand mentions a type's
    // internals, so this half compiles under any zig that has multi-object `for`.
    inline for ([_]type{ u8, u16, u32 }, 0..) |T, i| {
        const idx: i32 = @intCast(i);
        const w: i32 = @sizeOf(T);
        _ = printf("type    #%d is %d byte(s)\n", idx, w);
    }

    return @intCast(sumFields(Point, p));
}

// Comptime reflection: `@typeInfo(T)` — road-to-zig-std S5.
//
// `@typeInfo` is how Zig code asks a type about itself, and `switch (@typeInfo(T))` is the shape
// std reaches for 200 times over. dotcc answers it at LOWERING time: the `std.builtin.Type` value
// is synthesized straight from the resolved type (the union is compiler-known — the real compiler
// treats it the same way), the switch collapses to its one taken prong, and nothing of the query
// survives into the emitted C#. Every `kindName(...)` call below emits as a bare string constant.
//
// That folding is not an optimization, it is the only thing that works: each arm of such a switch
// is written for a DIFFERENT kind, so the arms a given T does not take would not lower at all.
//
// The interesting half is `bits`. dotcc widens an arbitrary-width `uN` to the smallest standard
// width — `u21` becomes a 32-bit `uint` — so the lowered type cannot answer it. The DECLARED width
// therefore travels with the type binding: spelled directly, through a `comptime T: type` param, or
// through an alias, `@typeInfo(…).int.bits` says 21. It also keys the instance, so `bitsOf(u21)` and
// `bitsOf(u32)` are separate specializations rather than one shared answer.
//
// What is still refused: an `anytype` param or `@TypeOf(expr)`, where the type is inferred from a
// VALUE and no spelling survives anywhere. dotcc raises a clear error there rather than answer 32
// where real zig says 21 — a missing feature is recoverable, a wrong comptime constant is not.
//
// NOTE: the `members` line below uses zig 0.17-dev's `field_names` / `field_values`, which replaced
// 0.16's `fields: []const StructField`. dotcc targets 0.17-dev (the version whose std it compiles),
// so building this with real zig needs 0.17-dev too — the rest of the file works on either.
//
//   dotnet run --project DotCC -c Release -- --emit=file examples/zig-typeinfo/main.zig > out.cs
//   dotnet out.cs   # prints the seven lines below, exits 42
//
// Output:
//   kind    u8=int f64=float bool=bool []u8=pointer ?u32=optional [4]u8=array P=struct
//   int     u21 bits=21 signed=0 | i7 bits=7 signed=1
//   generic bitsOf(u21)=21 bitsOf(u32)=32 bitsOf(Cp)=21
//   child   ?u32 holds a 4-byte value | [4]u8 has 4 elements
//   members P has 2 fields, first starts x | E has 4, third value is 2
//   ask     hasField(P,"x")=1 hasField(P,"q")=0 hasDecl(P,"sum")=1 | field(p,"x")=40
//   sum     40 + 2 = 42

extern fn printf(fmt: [*:0]const u8, ...) c_int;

const P = struct {
    x: i32,
    y: i32,
    /// A DECL rather than a field — `@hasDecl` finds it, `@hasField` does not.
    fn sum(self: P) i32 { return self.x + self.y; }
};

/// A spelled tag type. An INFERRED one is a loud cut: zig picks the smallest unsigned int that
/// holds the largest member, dotcc defaults to `int`, and reporting that would disagree on width.
const E = enum(u8) { a, b, c, d };

/// An alias for a narrow width — it carries the declared width like the spelling does, and keys the
/// same specialization as writing `u21` at the call site.
const Cp = u21;

/// The width through a comptime `type` param. `bitsOf(u21)` and `bitsOf(u32)` are DISTINCT
/// specializations: both resolve to a 32-bit `uint`, so keying by the lowered type alone would make
/// them one function with one answer.
fn bitsOf(comptime T: type) u16 {
    return @typeInfo(T).int.bits;
}

/// The type's kind as a printable name — the headline `switch (@typeInfo(T))` dispatch. Folded per
/// instantiation, so each call site emits the string constant directly.
fn kindName(comptime T: type) [*:0]const u8 {
    return switch (@typeInfo(T)) {
        .int => "int",
        .float => "float",
        .bool => "bool",
        .pointer => "pointer",
        .optional => "optional",
        .array => "array",
        .@"struct" => "struct",
        else => "other",
    };
}

/// A prong CAPTURE, and a payload field that IS exactly recoverable from the lowered type:
/// dotcc's width widening never changes a type's signedness, so this one is answered.
fn isSigned(comptime T: type) u8 {
    return switch (@typeInfo(T)) {
        .int => |i| if (i.signedness == .signed) 1 else 0,
        else => 0,
    };
}

pub fn main() u8 {
    _ = printf("kind    u8=%s f64=%s bool=%s []u8=%s ?u32=%s [4]u8=%s P=%s\n",
        kindName(u8), kindName(f64), kindName(bool), kindName([]u8),
        kindName(?u32), kindName([4]u8), kindName(P));

    // `bits` from the source spelling — 21 and 7, the DECLARED widths, not the 32/8 dotcc lowers
    // them to.
    const bits21: u16 = @typeInfo(u21).int.bits;
    const bits7: u16 = @typeInfo(i7).int.bits;
    _ = printf("int     u21 bits=%d signed=%d | i7 bits=%d signed=%d\n",
        bits21, isSigned(u21), bits7, isSigned(i7));

    // …and through a comptime `type` param, where the spelling reaches the callee on the seed.
    _ = printf("generic bitsOf(u21)=%d bitsOf(u32)=%d bitsOf(Cp)=%d\n",
        bitsOf(u21), bitsOf(u32), bitsOf(Cp));

    // `.child` is a TYPE, so it resolves in a type position; `.len` is a comptime integer.
    const Child = @typeInfo(?u32).optional.child;
    const len: u8 = @typeInfo([4]u8).array.len;
    const childSize: u8 = @sizeOf(Child);
    _ = printf("child   ?u32 holds a %d-byte value | [4]u8 has %d elements\n", childSize, len);

    // The parallel MEMBER LISTS this zig exposes — `field_names` / `field_types` / `field_values`
    // rather than a slice of field structs. `.len` and a comptime index both fold to literals.
    const nf: u8 = @typeInfo(P).@"struct".field_names.len;
    const names = @typeInfo(P).@"struct".field_names;
    const ne: u8 = @typeInfo(E).@"enum".field_names.len;
    const ev: u8 = @typeInfo(E).@"enum".field_values[2];
    _ = printf("members P has %d fields, first starts %c | E has %d, third value is %d\n",
        nf, names[0][0], ne, ev);

    // Membership needs no declaration ORDER, which is why these work where `decl_names` does not.
    var p = P{ .x = 40, .y = 2 };
    p.x += 0;
    const hx: u8 = if (@hasField(P, "x")) 1 else 0;
    const hq: u8 = if (@hasField(P, "q")) 1 else 0;
    const hd: u8 = if (@hasDecl(P, "sum")) 1 else 0;
    _ = printf("ask     hasField(P,\"x\")=%d hasField(P,\"q\")=%d hasDecl(P,\"sum\")=%d | field(p,\"x\")=%d\n",
        hx, hq, hd, @field(p, "x"));

    const a: Child = 40;
    const b: Child = @typeInfo([4]u8).array.len / 2;
    _ = printf("sum     %d + %d = %d\n", a, b, a + b);
    return @intCast(a + b);
}

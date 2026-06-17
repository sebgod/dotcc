// dotcc Zig front-end — TAGGED UNIONS `union(enum)` (Milestone D3). A tagged union pairs a
// discriminant (an auto-inferred enum tag) with a per-variant payload. dotcc lowers it to a
// managed DISCRIMINATED STRUCT: a synthesized tag enum `Shape_Tag`, a `__tag` discriminant
// field, and one struct field per payload variant (a void variant — `none` — contributes only a
// tag member). The non-overlapped layout matches the program's observable behavior (dotcc
// transpiles to C#; it does not reproduce Zig's union ABI).
//
// Construction sets the tag + the active variant:
//   * payload variant: `Shape{ .circle = r }` / `.{ .circle = r }`  → tag + the field
//   * void variant:    `.none`                                       → tag only
// A `switch` on the union dispatches on the tag; a `|x|` capture binds the matched variant's
// payload (by value). Deferred: untagged `union { … }`, explicit `union(SomeEnum)`, union
// methods, by-reference / mutable `|*x|` capture, multi-variant capture prongs.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-union/main.zig --emit=file -o out.cs
//             dotnet run out.cs                       # -> exit 42
//   real zig: zig build-exe main.zig && ./main         # -> same
const Shape = union(enum) {
    circle: u8, // radius
    square: u8, // side
    none, // a void / tag-only variant
};

// Switch with a payload capture per variant.
fn value(s: Shape) u8 {
    switch (s) {
        .circle => |r| {
            return r + 2;
        },
        .square => |side| {
            return side * side;
        },
        .none => {
            return 0;
        },
    }
}

pub fn main() u8 {
    const a = Shape{ .circle = 40 }; // payload variant → value = 42
    const b: Shape = .none; // void variant → value = 0
    return value(a) + value(b); // 42 + 0 = 42
}

// dotcc Zig front-end — top-level globals (`const` / `var`).
//
// A top-level `const` is an immutable comptime value; a top-level `var` is a mutable global.
// dotcc lowers each to a `public static` field of `DotCcGlobals`, surfaced by bare name — so a
// function body reads/writes it without qualification (the same path the C front-end's file-scope
// variables take). A `const` initializer may reference an EARLIER `const` by bare name.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-globals/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "score=42")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const BASE: u8 = 40; // an immutable, typed global constant
const STEP: u8 = 1; // a sibling const used as the increment
const START: u8 = STEP - STEP; // a const whose initializer references an earlier const (→ 0)

var hits: u8 = START; // a mutable global accumulator, seeded from a const

// Mutate the global by bare name (no qualification — `using static DotCcGlobals`).
fn record() void {
    hits += STEP;
}

pub fn main() u8 {
    record();
    record();
    const score: u8 = BASE + hits; // 40 + 2 = 42
    _ = printf("score=%d\n", @as(c_int, score));
    return score;
}

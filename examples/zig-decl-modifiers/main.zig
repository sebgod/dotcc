// Milestone R, part 5 — declaration modifiers: `callconv` / `align` / `linksection`.
//
// All three are pure NO-OPs on dotcc's managed target — a C# method has no controllable
// calling convention, and a C# field/local has no controllable alignment or link section.
// dotcc accepts them (so real Zig that uses them round-trips) and ignores them. Real zig
// honours them; both compile and run to the same result.

// A global with an explicit link section.
var counter: u32 linksection(".mydata") = 0;

// A function with an explicit calling convention.
fn tag(x: u8) callconv(.c) u8 {
    return x + 1;
}

pub fn main() u8 {
    // A local with an explicit alignment.
    var buf: u32 align(8) = 30;
    buf += tag(11); // 30 + 12 = 42
    counter = buf;
    return @intCast(counter);
}

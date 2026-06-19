// dotcc Zig front-end — bool + char literals.
//
// `true` / `false` are `bool` values (→ C# `true`/`false`, stored in the normalising `CBool`).
// A char literal `'x'` is a Zig `comptime_int` equal to the codepoint (→ an integer literal),
// with the common escapes: `\n` `\t` `\\` `\'` and `\xNN` hex.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-literals/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "ok")
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn putchar(c: c_int) c_int;

// `upper` is a bool flag; char arithmetic on the codepoints does the case fold. 'a' - 'A' is the
// comptime offset 32 between lower- and upper-case ASCII.
fn classify(upper: bool, ch: u8) u8 {
    if (upper) return ch - ('a' - 'A');
    return ch;
}

pub fn main() u8 {
    // char literals: 'o' = 111, 'k' = 107, '\n' = 10 → prints "ok".
    _ = putchar('o');
    _ = putchar('k');
    _ = putchar('\n');

    // a bool literal driving a branch, then a prefix-not on a bool literal.
    const shout: bool = true;
    const c = classify(shout, 'b'); // 'b' = 98 → upper → 98 - 32 = 66 = 'B'
    if (c == 'B' and !false) return '*'; // '*' = 42
    return 0;
}

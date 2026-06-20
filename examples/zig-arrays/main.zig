// dotcc Zig front-end — array literals & aggregate globals (Milestone K).
//
// `[N]T` / `[_]T` array literals: `.{…}` at a `[N]T` sink (length from the annotation), a typed
// `[N]T{…}` (explicit length), and a typed `[_]T{…}` (length inferred from the element count). A
// local array lowers to a `stackalloc`; a `[N]T` / `undefined` / struct GLOBAL lowers to a pinned,
// program-lifetime backing store (a stable `T*`) — the same store a C file-scope array uses.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-arrays/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

const Point = struct { x: u8, y: u8 };

// Aggregate globals: a literal array, an inferred-length array, an `undefined` array, a struct.
const primes: [3]u8 = .{ 2, 3, 5 };
const evens = [_]u8{ 4, 6 };
var scratch: [4]u8 = undefined;
const origin: Point = .{ .x = 1, .y = 2 };

pub fn main() u8 {
    // A local array literal — anonymous `.{…}` at a `[3]u8` sink → a stackalloc'd array.
    const local: [3]u8 = .{ 10, 11, 12 };
    // A typed inferred-length literal.
    const tail = [_]u8{ 7, 8 };

    scratch[0] = 9;
    scratch[1] = 0;

    var sum: u8 = 0;
    sum += primes[0] + primes[1] + primes[2]; // 10  (global literal array)
    sum += evens[0] + evens[1];               // 10  (global inferred array)
    sum += scratch[0] + scratch[1];           //  9  (global undefined array, mutated)
    sum += origin.x + origin.y;               //  3  (struct global)
    sum += local[0] + local[1] + local[2];    // 33  (local array literal)
    sum += tail[0] + tail[1];                 // 15  (local inferred array)

    _ = printf("primes=%d evens=%d scratch=%d origin=%d local=%d tail=%d\n",
        @as(c_int, primes[0] + primes[1] + primes[2]),
        @as(c_int, evens[0] + evens[1]),
        @as(c_int, scratch[0] + scratch[1]),
        @as(c_int, origin.x + origin.y),
        @as(c_int, local[0] + local[1] + local[2]),
        @as(c_int, tail[0] + tail[1]));

    // 10 + 10 + 9 + 3 + 33 + 15 = 80; narrow back to u8 and subtract 38 = 42.
    return sum - 38;
}

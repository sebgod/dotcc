// dotcc Zig front-end — NON-ZERO sentinel arrays `[N:s]T` (Milestone Z).
//
// Milestone O added sentinel-terminated arrays `[N:0]T` (the C-string shape) with a ZERO sentinel —
// the trailing slot rode C#'s zero-fill. Milestone Z lifts that to any compile-time sentinel value:
// the symbol keeps the LOGICAL `[N]T` type (so `.len`/slicing exclude the sentinel), but the decl
// reserves N+1 storage slots and materializes the sentinel `s` in the trailing slot — appended to an
// array literal (`stackalloc i32[]{ …, s }`), or written explicitly for an `undefined` array (where
// C#'s zero-fill would otherwise leave 0). The sentinel is readable at index `len`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-sentinel-nonzero/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig && ./main ; echo $?

pub fn main() u8 {
    // `[3:9]i32` — three data elements, sentinel 9 in the trailing slot (readable at a[3]).
    const a: [3:9]i32 = .{ 10, 11, 12 };
    // `[2:5]i32` — two data elements, sentinel 5.
    const b: [2:5]i32 = .{ 14, 14 };

    var total: i32 = a[3]; // the sentinel -> 9
    total = total + b[2]; // the sentinel -> +5 = 14
    total = total + b[0] + b[1]; // +28 = 42
    return @intCast(total);
}

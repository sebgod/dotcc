// dotcc Zig front-end — many-item pointers `[*]T` / `[*]const T` (Milestone O, part 2).
//
// `[*]T` points at an unknown-length run of `T`: it supports indexing (`p[i]`) and slicing
// (`p[lo..hi]`) but NOT `.len` (no known length). Like `[*c]T`, it lowers to a bare `T*`; the
// type-level distinction from `[*c]` (non-null, no C-conversion) is not modeled. A slice's
// `.ptr` IS a `[*]T`, so it binds straight across; an array decays to one.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-many-ptr/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42
//   real zig: zig build-exe main.zig -lc && ./main ; echo $?

extern fn printf(format: [*c]const u8, ...) c_int;

// index a many-item pointer.
fn first(p: [*]const u8) u8 {
    return p[0];
}

// closed-slice a many-item pointer back into a length-carrying slice.
fn lenOf3(p: [*]const u8) usize {
    const sl = p[0..3];
    return sl.len;
}

pub fn main() u8 {
    const s: []const u8 = "*bcdef"; // s.ptr[0] = '*' = 42
    const p: [*]const u8 = s.ptr; // a slice's `.ptr` is a `[*]const u8`
    if (lenOf3(p) != 3) return 1; // closed slice of a `[*]T` -> .len
    const c = first(p); // 42
    _ = printf("c=%u\n", c);
    return c; // 42
}

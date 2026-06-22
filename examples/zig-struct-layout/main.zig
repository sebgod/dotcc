// Milestone R, part 2 — struct layout modifiers `extern struct` / `packed struct`.
//
// `extern struct` pins guaranteed C-ABI layout (natural alignment + tail padding);
// `packed struct` removes inter-field padding. dotcc emits [StructLayout(Sequential)]
// and [StructLayout(Sequential, Pack=1)] respectively, and computes a matching
// compile-time `@sizeOf`. Both kinds support ordinary field read/write.
//
// V1 note: dotcc byte-packs a `packed struct` (Pack=1). `@sizeOf` matches Zig's
// bit-backing-integer model only when fields are byte-multiples summing to an ABI
// size (here Pk = 4×u8 = 32 bits → 4 bytes on both); sub-byte bit-packed fields are
// a documented cut.

const Ext = extern struct {
    a: u8,
    b: u32,
};

const Pk = packed struct {
    a: u8,
    b: u8,
    c: u8,
    d: u8,
};

pub fn main() u8 {
    var e: Ext = .{ .a = 3, .b = 7 };
    e.b += 4; // b = 11

    var p: Pk = .{ .a = 1, .b = 2, .c = 3, .d = 4 };
    p.d += 6; // d = 10

    const sz: u32 = @sizeOf(Ext) + @sizeOf(Pk); // 8 + 4 = 12

    // 3 + 11 + 1 + 2 + 3 + 10 + 12 = 42
    const total: u32 = @as(u32, e.a) + e.b + @as(u32, p.a) + @as(u32, p.b) + @as(u32, p.c) + @as(u32, p.d) + sz;
    return @intCast(total);
}

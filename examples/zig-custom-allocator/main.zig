// Milestone W, part 1b — a user-constructed custom `std.mem.Allocator`.
//
// A hand-written bump allocator: its state (`Bump`) lives behind the opaque `ctx` pointer, and the
// real 4-function vtable (`alloc`/`resize`/`remap`/`free`, the exact `std.mem.Allocator.VTable`
// shape, each carrying `std.mem.Alignment` + `[]u8` + `ret_addr`) is bound to the four functions.
// `main` constructs `std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable }` and uses the
// standard `a.alloc` / `a.free` surface — dispatch flows through the vtable to these functions.
//
// alloc 10 bytes, fill 0..9 (sum 45), free → exit 45 - 3 = 42.

const std = @import("std");

const Bump = struct {
    base: [*]u8,
    cap: usize,
    used: usize,
};

fn bumpAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {
    _ = alignment;
    _ = ret_addr;
    const self: *Bump = @ptrCast(@alignCast(ctx));
    if (self.used + len > self.cap) return null;
    const p = self.base + self.used;
    self.used += len;
    return p;
}

fn bumpResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {
    _ = ctx;
    _ = memory;
    _ = alignment;
    _ = new_len;
    _ = ret_addr;
    return false;
}

fn bumpRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {
    _ = ctx;
    _ = memory;
    _ = alignment;
    _ = new_len;
    _ = ret_addr;
    return null;
}

fn bumpFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {
    _ = ctx;
    _ = memory;
    _ = alignment;
    _ = ret_addr;
}

const bump_vtable = std.mem.Allocator.VTable{
    .alloc = bumpAlloc,
    .resize = bumpResize,
    .remap = bumpRemap,
    .free = bumpFree,
};

pub fn main() u8 {
    var backing: [256]u8 = undefined;
    var state = Bump{ .base = backing[0..].ptr, .cap = backing.len, .used = 0 };
    const a = std.mem.Allocator{ .ptr = &state, .vtable = &bump_vtable };

    const buf = a.alloc(u8, 10) catch return 1;
    var i: usize = 0;
    while (i < 10) : (i = i + 1) {
        buf[i] = @intCast(i);
    }
    var sum: u32 = 0;
    i = 0;
    while (i < 10) : (i = i + 1) {
        sum += buf[i];
    }
    a.free(buf);
    return @intCast(sum - 3); // 45 - 3 = 42
}

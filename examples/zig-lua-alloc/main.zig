// Milestone W, part 2 — a C `lua_Alloc` behind a Zig `std.mem.Allocator` (the deep bridge).
//
// Real zig has NO automatic "C fn-pointer → std.mem.Allocator" coercion: to consume a `lua_Alloc`
// you HAND-WRITE an adapter — a `std.mem.Allocator{ .ptr, .vtable }` whose 4-function vtable calls
// the imported C allocator. This is exactly that adapter, and dotcc lowers + dispatches it
// identically to real zig (the mixed `.c` + `.zig` differential oracle proves it).
//
// `lua_alloc` lives in lua_alloc.c and binds across the seam by bare name (Milestone V). The opaque
// `ctx` carries the host userdata (`*usize` byte-counter) straight through to `lua_alloc`'s `ud`.
//
// alloc 10 bytes (counter += 10), fill 0..9 (sum 45), free → exit 45 + 10 - 13 = 42.

const std = @import("std");

extern fn lua_alloc(ud: ?*anyopaque, ptr: ?*anyopaque, osize: usize, nsize: usize) ?*anyopaque;

fn luaAlloc(ctx: *anyopaque, len: usize, alignment: std.mem.Alignment, ret_addr: usize) ?[*]u8 {
    _ = alignment;
    _ = ret_addr;
    return @ptrCast(lua_alloc(ctx, null, 0, len));
}

fn luaResize(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) bool {
    _ = ctx;
    _ = memory;
    _ = alignment;
    _ = new_len;
    _ = ret_addr;
    return false;
}

fn luaRemap(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, new_len: usize, ret_addr: usize) ?[*]u8 {
    _ = ctx;
    _ = memory;
    _ = alignment;
    _ = new_len;
    _ = ret_addr;
    return null;
}

fn luaFree(ctx: *anyopaque, memory: []u8, alignment: std.mem.Alignment, ret_addr: usize) void {
    _ = alignment;
    _ = ret_addr;
    _ = lua_alloc(ctx, memory.ptr, memory.len, 0);
}

const lua_vtable = std.mem.Allocator.VTable{
    .alloc = luaAlloc,
    .resize = luaResize,
    .remap = luaRemap,
    .free = luaFree,
};

pub fn main() u8 {
    var bytes: usize = 0;
    const a = std.mem.Allocator{ .ptr = &bytes, .vtable = &lua_vtable };

    const buf = a.alloc(u8, 10) catch return 1;
    var i: usize = 0;
    while (i < 10) : (i = i + 1) {
        buf[i] = @intCast(i);
    }
    var sum: usize = 0;
    i = 0;
    while (i < 10) : (i = i + 1) {
        sum += buf[i];
    }
    a.free(buf);
    return @intCast(sum + bytes - 13); // 45 + 10 - 13 = 42
}

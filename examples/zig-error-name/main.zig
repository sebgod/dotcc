// Milestone X, part 1 — `@errorName`: the un-erased error name as `[]const u8`.
//
// dotcc erases the error SET to one flat `ushort` code, but now carries the code→name table into
// the emit (a `__zigErrorName(code)` helper), so `@errorName(e)` returns the real name — exactly as
// real zig does (whose `@errorName` yields a `[:0]const u8`). `@errorName(error.Ok)` = "Ok"
// (bytes 'O' = 79, 'k' = 107; len 2). The exit code is content-sensitive — it reads both a name
// byte and the length: @as(usize, name[0]) + name.len - 39 = 79 + 2 - 39 = 42.

pub fn main() u8 {
    const name = @errorName(error.Ok);
    return @intCast(@as(usize, name[0]) + name.len - 39);
}

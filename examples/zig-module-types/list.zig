// A type-returning generic in its own module — the shape `std.ArrayList(T)` has: `std.ArrayList` is
// `array_list.Aligned(T, null)`, a generic that lives in one file and is instantiated from another.
// Each instantiation reifies its own struct, with its own method set, in THIS module's environment;
// the type argument is resolved at the call site, in the caller's.

pub fn Box(comptime T: type) type {
    return struct {
        first: T,
        len: usize,

        const Self = @This();

        pub fn init(v: T) Self {
            return .{ .first = v, .len = 1 };
        }

        pub fn bump(self: *Self, by: usize) void {
            self.len = self.len + by;
        }

        // `self.first` is a `T`, so it needs an explicit cast to be added to a `usize`: zig widens
        // implicitly only when every value of the source type fits the target (u8 → usize does,
        // i32 → usize does not, since the sign would be lost).
        pub fn total(self: *const Self) usize {
            return self.len + @as(usize, @intCast(self.first));
        }
    };
}

// A FILE that is a struct type: the fields below sit at file scope, so this file IS the `Counter`
// type, and its top-level functions are its methods (`std.Io.Writer`'s shape).
const Counter = @This();

count: u8,
step: u8 = 1,

pub fn init(start: u8) Counter {
    return .{ .count = start };
}

pub fn tick(c: *Counter) void {
    c.count += c.step;
}

pub fn value(c: *const Counter) u8 {
    return c.count;
}

// An ordinary module: a struct with a method, and an enum. Nothing here is special — the point is
// that `main.zig` can name BOTH from across the `@import` seam.

pub const Axis = enum { x, y };

pub const Point = struct {
    x: i32,
    y: i32,

    pub fn get(self: Point, a: Axis) i32 {
        return switch (a) {
            .x => self.x,
            .y => self.y,
        };
    }

    pub fn sum(self: Point) i32 {
        return self.x + self.y;
    }
};

// A FILE used as a struct type, reached the way real std reaches `std.Io.Writer`
// (road-to-zig-std G3).
//
// `Counter.zig` declares its fields at file scope, so the file itself is the `Counter` type:
//   - `var a: Counter = .init(40)`  a decl literal: `Counter`'s own top-level `init`
//   - `a.tick()`                    a method call: the file's top-level `tick`, receiver `&a`
//   - `names.Counter`               the same type through a re-exporting module, bound to an
//                                   alias (`const Writer = std.Io.Writer;` is this shape)
//
// Each module's functions emit under its prefix (`Counter__init`), so the root's own `init`
// below does not collide with the file's.
//
//   dotnet run --project DotCC -c Release -- --emit=file examples/zig-file-struct/main.zig > out.cs
//   dotnet out.cs   # prints the two lines below, exits 42
//
// Output:
//   a=41
//   b=1
const std = @import("std");
const names = @import("names.zig");
const Counter = names.Counter;

fn init() u8 {
    return 0;
}

pub fn main() u8 {
    var a: Counter = .init(40);
    a.tick();
    var b = Counter.init(init());
    b.tick();
    std.debug.print("a={d}\n", .{a.value()});
    std.debug.print("b={d}\n", .{b.value()});
    return a.value() + b.value();
}

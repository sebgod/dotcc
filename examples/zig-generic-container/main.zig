// A generic CONTAINER with methods — a type-returning generic whose reified struct carries
// `const` members and methods, not just fields (road-to-zig-std G4).
//
// `Stack(T, cap)` is a comptime type constructor: each distinct (T, cap) pair reifies its own
// struct with its own method set, monomorphized at compile time. There is no runtime generic
// dispatch and no boxing — `ByteStack.push` and `IntStack.push` are two separate emitted
// functions over `byte` and `int`. This is the shape `std.ArrayList` has, on a fixed buffer.
//
//   dotnet run --project DotCC -c Release -- --emit=file examples/zig-generic-container/main.zig > out.cs
//   dotnet out.cs   # prints the three lines below, exits 42
//
// Output:
//   u8   n=2 cap=8 sum=42 top=32 left=1
//   i32  n=3 cap=4 sum=100 max=105
//   reified: ByteStack and IntStack are independent types

extern fn printf(fmt: [*:0]const u8, ...) c_int;

fn Stack(comptime T: type, comptime cap: usize) type {
    return struct {
        items: [cap]T,
        len: usize,

        // `@This()` is the in-progress reified type, so `Self` names *this* instantiation —
        // `Stack(u8, 8)` and `Stack(i32, 4)` each get their own.
        const Self = @This();

        // A `const` member is comptime and namespaced to the container; read as `Self.CAP`.
        const CAP = cap;

        // A receiverless method is the constructor idiom (`ArrayList.init`/`.empty`); it is
        // reachable through the alias the instantiation is bound to — `ByteStack.init()`.
        pub fn init() Self {
            var s: Self = undefined;
            s.len = 0;
            return s;
        }

        pub fn push(self: *Self, v: T) void {
            self.items[self.len] = v;
            self.len = self.len + 1;
        }

        pub fn pop(self: *Self) T {
            self.len = self.len - 1;
            return self.items[self.len];
        }

        pub fn count(self: *const Self) usize {
            return self.len;
        }

        pub fn capacity(self: *const Self) usize {
            _ = self;
            return Self.CAP;
        }

        // A method may call a sibling method on the same instantiation (`self.count()` below)
        // and declare locals typed by the type parameter (`var total: T`).
        pub fn sum(self: *const Self) T {
            var total: T = 0;
            var i: usize = 0;
            while (i < self.count()) : (i = i + 1) {
                total = total + self.items[i];
            }
            return total;
        }

        pub fn max(self: *const Self) T {
            var best: T = self.items[0];
            var i: usize = 1;
            while (i < self.count()) : (i = i + 1) {
                if (self.items[i] > best) { best = self.items[i]; }
            }
            return best;
        }
    };
}

const ByteStack = Stack(u8, 8);
const IntStack = Stack(i32, 4);

pub fn main() u8 {
    var bs = ByteStack.init();
    bs.push(10);
    bs.push(32);
    const s = bs.sum();
    const top = bs.pop();
    _ = printf("u8   n=%llu cap=%llu sum=%d top=%d left=%llu\n", bs.count() + 1, bs.capacity(), s, top, bs.count());

    var is = IntStack.init();
    is.push(-5);
    is.push(105);
    is.push(0);
    _ = printf("i32  n=%llu cap=%llu sum=%d max=%d\n", is.count(), is.capacity(), is.sum(), is.max());

    _ = printf("reified: ByteStack and IntStack are independent types\n");
    return s;
}

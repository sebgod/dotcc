// dotcc Zig front-end — single-object allocation `create` / `destroy` (Milestone U).
//
// `a.create(T)` allocates ONE `T` and returns Zig's `Error!*T`; `a.destroy(p)` frees it. dotcc
// represents `Error!*T` as `ErrUnion<nuint>` — a pointer can't be an `ErrUnion<T>` generic
// argument (`T : unmanaged` excludes pointer types), so the address rides as a `nuint` and
// `try a.create(T)` casts the unwrapped address back to `*T`. On the statically-known default the
// call DEVIRTUALIZES to a direct `Libc.malloc` / `free` (no vtable); on an opaque allocator it
// dispatches through the vtable, exactly like `alloc` / `free`.
//
//   dotcc:    dotnet run --project DotCC -c Release -- examples/zig-create/main.zig --emit=file -o out.cs
//             dotnet run out.cs ; echo $?            # -> 42  (prints "sum=42")
//   real zig: zig build-exe main.zig && ./main ; echo $?
const std = @import("std");

extern fn printf(format: [*c]const u8, ...) c_int;

const Node = struct {
    value: i32,
    next: ?*Node,
};

// Build a two-node list on the heap, sum it, then tear it down — the headline create/destroy use.
fn run() !u8 {
    const a = std.heap.page_allocator;

    const head = try a.create(Node); // devirtualized: a direct malloc(sizeof(Node))
    head.value = 30;

    const tail = try a.create(Node);
    tail.value = 12;
    tail.next = null;
    head.next = tail;

    const sum = head.value + head.next.?.value; // 30 + 12 = 42
    _ = printf("sum=%d\n", @as(c_int, sum));

    a.destroy(tail); // devirtualized: a direct free
    a.destroy(head);

    const result: u8 = @intCast(sum); // a typed binding gives @intCast its result location
    return result;
}

pub fn main() u8 {
    return run() catch 1;
}

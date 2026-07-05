#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for the curated <c>std.ArrayList(T)</c> (wall-plan W0) — the modern UNMANAGED
/// array list (zig 0.15+): <c>std.ArrayList(i32)</c> resolves in TYPE position through the
/// ordinary Suffix chain (no grammar change), <c>.empty</c> lowers to <c>default</c>, the
/// curated member set routes to the runtime <c>ZigList&lt;T&gt;</c> instance methods
/// (<c>append</c>/<c>appendSlice</c> → <c>!void</c> so <c>try</c> composes, <c>pop</c> →
/// <c>?T</c>, <c>items</c> → a mutable slice), and every unmodeled member / decl literal /
/// managed-API shape (<c>init(alloc)</c>) is a loud error. End-to-end differential in the
/// <c>arraylist</c> zig-oracle program. Runtime behavior is pinned separately in
/// <see cref="ZigListRuntimeTests"/>.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigArrayListTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ziglist-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string Prelude = "const std = @import(\"std\");\n";

    [Fact]
    public void Type_position_resolves_and_empty_lowers_to_default()
    {
        // `var list: std.ArrayList(i32) = .empty;` — the call in TYPE position parses via
        // the ordinary Suffix chain and resolves to the runtime ZigList<int>; `.empty` is
        // exactly `default` (null ptr, zero len/capacity).
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(i32) = .empty;
                _ = list;
                return 0;
            }
            """);
        cs.ShouldContain("ZigList<int> list = default(ZigList<int>);");
    }

    [Fact]
    public void Append_routes_to_the_runtime_with_the_oom_code_and_try_composes()
    {
        // `try list.append(alloc, v)` → ErrUnion.Try(list.Append(<allocator>, v, <oom>)) —
        // the !void error union composes with the existing try machinery, and the
        // statically-known c_allocator materializes as the C-heap fat pointer.
        var cs = EmitZig(Prelude + """
            pub fn main() !void {
                const alloc = std.heap.c_allocator;
                var list: std.ArrayList(i32) = .empty;
                try list.append(alloc, 42);
                list.deinit(alloc);
            }
            """);
        cs.ShouldContain(".Append(");
        cs.ShouldContain("ErrUnion.Try(");
        cs.ShouldContain(".Deinit(");
        cs.ShouldContain("ZigAlloc.CHeap()");
    }

    [Fact]
    public void Items_and_capacity_lower_to_the_runtime_members()
    {
        // `list.items` → the runtime Items property (a mutable Slice<T>, so subscript and
        // `.len` ride the ordinary slice lowering); `list.capacity` → the Cap field.
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(u8) = .empty;
                if (list.items.len != 0) { return 1; }
                if (list.capacity != 0) { return 2; }
                return 0;
            }
            """);
        cs.ShouldContain("list.Items.Len");
        cs.ShouldContain("list.Cap");
    }

    [Fact]
    public void Pop_lowers_to_a_nullable_and_the_optional_machinery_applies()
    {
        // `list.pop()` returns ?T (zig 0.15+) → int?; the existing value-optional
        // machinery (`if (opt) |v|`) captures the payload.
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(i32) = .empty;
                const last = list.pop();
                if (last) |v| { return @intCast(v); }
                return 0;
            }
            """);
        cs.ShouldContain("int? last = list.Pop();");
    }

    [Fact]
    public void For_over_items_rides_the_slice_loop_lowering()
    {
        // `for (list.items) |x|` — items is slice-typed, so the existing slice-for
        // lowering applies unchanged (a counted loop over the fat pointer).
        var cs = EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(i32) = .empty;
                var sum: i32 = 0;
                for (list.items) |x| { sum = sum + x; }
                return @intCast(sum);
            }
            """);
        cs.ShouldContain("list.Items");
        cs.ShouldContain("__i");     // the synthesized loop counter of the slice-for lowering
    }

    [Fact]
    public void Managed_api_init_is_rejected_loudly()
    {
        // The pre-0.15 MANAGED API (`std.ArrayList(i32).init(alloc)` + allocator-less
        // calls) no longer exists in the pinned zig — dotcc rejects it rather than model
        // a surface the reference compiler refuses.
        var ex = Should.Throw<Exception>(() => EmitZig(Prelude + """
            pub fn main() u8 {
                var list = std.ArrayList(i32).init(std.heap.c_allocator);
                _ = list;
                return 0;
            }
            """));
        ex.Message.ShouldContain("std.ArrayList");
    }

    [Fact]
    public void Unmodeled_member_and_decl_literal_are_rejected_loudly()
    {
        // An un-curated method names the curated set…
        var ex = Should.Throw<Exception>(() => EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(i32) = .empty;
                list.shrinkAndFree(std.heap.c_allocator, 0);
                return 0;
            }
            """));
        ex.Message.ShouldContain("no modeled member 'shrinkAndFree'");
        ex.Message.ShouldContain("curated:");

        // …and so does an unknown decl literal (only `.empty` is modeled).
        var ex2 = Should.Throw<Exception>(() => EmitZig(Prelude + """
            pub fn main() u8 {
                var list: std.ArrayList(i32) = .full;
                _ = list;
                return 0;
            }
            """));
        ex2.Message.ShouldContain("only `.empty`");
    }

    [Fact]
    public void Non_allocator_first_argument_is_rejected_loudly()
    {
        // The unmanaged API takes the allocator per call — passing something else is a
        // clear error (not a silent mis-bind).
        var ex = Should.Throw<Exception>(() => EmitZig(Prelude + """
            pub fn main() !void {
                var list: std.ArrayList(i32) = .empty;
                try list.append(1, 42);
            }
            """));
        ex.Message.ShouldContain("std.mem.Allocator");
    }
}

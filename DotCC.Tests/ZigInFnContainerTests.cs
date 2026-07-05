#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for in-function container declarations (wall-plan W2): a <c>const P = struct { … };</c>
/// inside a function body. The grammar admits a <c>ContainerDecl</c> in statement position
/// (conflict-free with the Decl-level form); lowering registers the struct into the module type
/// section ON THE FLY (top-level containers pre-register in pass 0; a local one is first seen during
/// body lowering) under a function-mangled IR name (<c>&lt;fn&gt;__&lt;P&gt;</c>) so two bodies' like-named
/// locals never collide, and maps the plain name to that type (shadow-saved, restored at body exit) so
/// it does not leak into a sibling function. Emits no runtime decl. V1 is struct-only, fields-only —
/// a local enum/union or a method/const member is a loud cut. End-to-end in the <c>in-fn-struct</c>
/// zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigInFnContainerTests
{
    private static string EmitZig(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-zigfnc-{Guid.NewGuid():N}.zig");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Local_struct_registers_under_a_function_mangled_name()
    {
        // `const P = struct {…}` in main → the IR type `main__P`; the plain name resolves to it for
        // the following `var p: P`, the `.{…}` init, and `p.field` reads.
        var cs = EmitZig("""
            pub fn main() u8 {
                const P = struct { x: i32, y: i32 };
                const p: P = .{ .x = 3, .y = 4 };
                return @intCast(p.x + p.y);
            }
            """);
        cs.ShouldContain("main__P");
    }

    [Fact]
    public void Local_struct_self_reference_resolves_to_the_mangled_type()
    {
        // A self-referential field (`next: *Node`) resolves through the plain→mangled mapping that
        // is installed BEFORE the layout is registered, so the pointer field is `main__Node*`.
        var cs = EmitZig("""
            pub fn main() u8 {
                const Node = struct { next: *Node, val: i32 };
                const n: Node = .{ .next = undefined, .val = 9 };
                return @intCast(n.val);
            }
            """);
        cs.ShouldContain("main__Node");
        cs.ShouldContain("main__Node*");
    }

    [Fact]
    public void Same_local_name_in_two_functions_gets_distinct_mangled_types()
    {
        // Each function's local `P` mangles with its own name — no IR collision, distinct layouts.
        var cs = EmitZig("""
            fn fa() i32 { const P = struct { x: i32 }; const p: P = .{ .x = 1 }; return p.x; }
            fn fb() i64 { const P = struct { y: i64 }; const p: P = .{ .y = 2 }; return p.y; }
            pub fn main() u8 { return 0; }
            """);
        cs.ShouldContain("fa__P");
        cs.ShouldContain("fb__P");
    }

    [Fact]
    public void Local_container_does_not_leak_over_a_top_level_type_of_the_same_name()
    {
        // A local `Point` shadows the top-level `Point` only within its own body; a LATER function
        // (lowered after it in pass 2) that reads the top-level `Point` in its BODY still resolves
        // the unmangled type — the shadow is restored at body exit. `withTop`'s `.a` reads a field
        // that only the top-level `Point` has, so a leaked local would fail to compile.
        var cs = EmitZig("""
            const Point = struct { a: i32 };
            fn withLocal() i32 { const Point = struct { b: i32 }; const q: Point = .{ .b = 5 }; return q.b; }
            fn withTop() i32 { const p: Point = .{ .a = 7 }; return p.a; }
            pub fn main() u8 { return 0; }
            """);
        cs.ShouldContain("withLocal__Point");   // the local mangled type
    }

    [Fact]
    public void Method_in_a_local_struct_is_rejected_loudly()
    {
        // A method inside a local container needs the pass-1 free-function machinery only the
        // top-level passes run — a clear V1 cut, not a silent drop.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const P = struct {
                    x: i32,
                    fn get(self: P) i32 { return self.x; }
                };
                const p: P = .{ .x = 1 };
                return @intCast(p.x);
            }
            """));
        ex.Message.ShouldContain("fields-only");
    }

    [Fact]
    public void Local_enum_is_rejected_loudly()
    {
        // The grammar admits a local enum/union, but V1 lowers struct only.
        var ex = Should.Throw<Exception>(() => EmitZig("""
            pub fn main() u8 {
                const E = enum { a, b };
                return 0;
            }
            """));
        ex.Message.ShouldContain("struct-only");
    }
}

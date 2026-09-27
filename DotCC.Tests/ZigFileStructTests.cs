#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for a FILE used as a struct type (road-to-zig-std G3, the wall after cross-module
/// generics on the way to <c>std.fmt.bufPrint</c>): <c>Io/Writer.zig</c> declares its fields at file
/// scope, so the file itself is the <c>Writer</c> type and its top-level functions are its methods.
/// Also pins what reaching it through real std needed alongside: a module ALIAS
/// (<c>const math = std.math;</c>) navigates like the import it names, and a container of an imported module
/// that cannot lower fails only when something names it.
/// End-to-end in the <c>import_file_struct</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigFileStructTests
{
    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigfs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Box = """
        const Box = @This();

        value: u8,
        bump: u8 = 1,

        pub fn init(v: u8) Box {
            return .{ .value = v };
        }

        pub fn get(b: *const Box) u8 {
            return b.value + b.bump;
        }

        pub fn add(b: *Box, n: u8) void {
            b.value += n;
        }
        """;

    [Fact]
    public void An_imported_file_with_top_level_fields_is_a_struct_type()
    {
        var cs = EmitZigMulti("""
            const Box = @import("Box.zig");
            pub fn main() u8 {
                var b: Box = .init(30);
                b.add(11);
                const c = Box.init(0);
                return b.get() + c.get() - 1;
            }
            """, ("Box.zig", Box));
        // The file's fields are one struct, under the module-qualified name; the default is honored.
        cs.ShouldContain("struct Box__Box");
        cs.ShouldContain("new Box__Box { value = v, bump = 1 }");
        // A decl literal, a static call and a method call all reach the file's own top-level function.
        cs.ShouldContain("Box__Box b = Box__init(30);");
        cs.ShouldContain("Box__Box c = Box__init(0);");
        cs.ShouldContain("Box__add(&b, 11)");
        cs.ShouldContain("static unsafe Box__Box Box__init(byte v)");
    }

    [Fact]
    public void A_module_alias_names_a_file_struct_type_and_navigates_like_its_import()
    {
        // `const Writer = std.Io.Writer;` / `const math = std.math;`: std reaches almost every other
        // file through an alias of a module PATH, not an `@import` of its own.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            const Box = lib.Box;
            const ops = lib.ops;
            pub fn main() u8 {
                const b: Box = .init(40);
                return b.get() + ops.one();
            }
            """,
            ("lib.zig", "pub const Box = @import(\"Box.zig\");\npub const ops = @import(\"ops.zig\");\n"),
            ("Box.zig", Box),
            ("ops.zig", "pub fn one() u8 {\n    return 1;\n}\n"));
        cs.ShouldContain("Box__Box b = Box__init(40);");
        cs.ShouldContain("ops__one()");
    }

    [Fact]
    public void An_imported_container_that_cannot_lower_fails_only_when_named()
    {
        // zig analyses a declaration only when something references it; `std.Io`'s `Limit` enum needs
        // the comptime engine, and a program writing to a Writer never touches it.
        const string util = "pub const Bad = struct { x: NoSuchType };\npub fn ok() u8 {\n    return 42;\n}\n";
        var cs = EmitZigMulti("""
            const util = @import("util.zig");
            pub fn main() u8 {
                return util.ok();
            }
            """, ("util.zig", util));
        cs.ShouldContain("util__ok()");

        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const util = @import("util.zig");
            pub fn main() u8 {
                const b: util.Bad = undefined;
                _ = b;
                return 0;
            }
            """, ("util.zig", util)));
        ex.Message.ShouldContain("zig container `Bad` could not be lowered");
        ex.Message.ShouldContain("NoSuchType");
    }

    private const string BoxGeneric = Box + """

        pub fn addAny(b: *Box, comptime k: u8, n: anytype) void {
            b.value += k + n;
        }
        """;

    [Fact]
    public void A_generic_function_called_on_a_file_struct_instance_instantiates_with_the_receiver()
    {
        // `w.print(fmt, args)`'s shape: the receiver fills the runtime first parameter, the comptime and
        // `anytype` arguments key the instance, and the call is a method call on the instance.
        var cs = EmitZigMulti("""
            const Box = @import("Box.zig");
            pub fn main() u8 {
                var b: Box = .init(30);
                b.addAny(4, @as(u8, 2));
                b.addAny(4, @as(u8, 1));
                return b.value;
            }
            """, ("Box.zig", BoxGeneric));
        cs.ShouldContain("Box__addAny__4_u8(&b, (byte)2);");
        cs.ShouldContain("Box__addAny__4_u8(&b, (byte)1);");
        cs.ShouldContain("static unsafe void Box__addAny__4_u8(Box__Box* b, byte n)");
    }

    [Fact]
    public void A_generic_function_called_statically_through_a_file_struct_type_instantiates()
    {
        // Through the import (`Box.addAny(&b, …)`, module navigation) and, inside the module, through its
        // `@This()` alias (a container type, whose static call binds the template's placeholder signature
        // unless it is routed to the owner's generic instantiation).
        var cs = EmitZigMulti("""
            const Box = @import("Box.zig");
            pub fn main() u8 {
                var b: Box = .init(38);
                Box.addAny(&b, 1, @as(u8, 1));
                b.twice();
                return b.value;
            }
            """, ("Box.zig", BoxGeneric + """

            pub fn twice(b: *Box) void {
                Box.addAny(b, 1, @as(u8, 0));
            }
            """));
        cs.ShouldContain("Box__addAny__1_u8(&b, (byte)1);");
        cs.ShouldContain("Box__addAny__1_u8(b, (byte)0);");
    }

    [Fact]
    public void A_file_struct_method_the_parse_skipped_raises_its_parse_error()
    {
        // `Writer.print` was reported as "no method 'print'", hiding a parse wall inside its body.
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const Box = @import("Box.zig");
            pub fn main() u8 {
                var b: Box = .init(40);
                b.broken();
                return b.value;
            }
            """, ("Box.zig", Box + "\npub fn broken(b: *Box) void {\n    b.value += ;\n}\n")));
        ex.Message.ShouldContain("zig `broken` in Box.zig did not parse");
    }

    [Fact]
    public void A_function_local_alias_of_a_module_or_its_type_binds_at_compile_time()
    {
        // Task #181: `const P = lib.Pair;` and `const L = lib;` inside a body name a type and a module, which have no runtime
        // value; they are bound at compile time and the decls dropped (the body had lowered each as a value and failed).
        // zig returns 17.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                const P = lib.Pair;
                const L = lib;
                const p = P{ .a = 3, .b = 4 };
                return p.sum() + L.twice(5);
            }
            """, ("lib.zig", """
            pub const Pair = struct {
                a: u8,
                b: u8,
                pub fn sum(self: Pair) u8 {
                    return self.a + self.b;
                }
            };
            pub fn twice(x: u8) u8 {
                return x * 2;
            }
            """));
        cs.ShouldContain("lib__Pair p = new lib__Pair { a = 3, b = 4 };");
        cs.ShouldContain("return (byte)(lib__Pair_sum(p) + lib__twice(5));");
    }

    [Fact]
    public void A_call_to_a_module_tombstone_raises_its_compile_error()
    {
        // Task #186 (std.meta.fields in the 0.17 std): a `@compileError` tombstone called through its module reports the
        // tombstone's message, as zig does; it had been "has no exported function".
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                return lib.old(1);
            }
            """, ("lib.zig", """
            pub const old = @compileError("Deprecated; use 'fresh' instead");
            pub fn fresh(x: u8) u8 {
                return x + 1;
            }
            """)));
        ex.Message.ShouldContain("Deprecated; use 'fresh' instead");
    }

    [Fact]
    public void An_unreferenced_method_of_a_reified_generic_is_never_lowered()
    {
        // Task #190 (std.PriorityDequeue's debugging `dump`): zig analyses only the functions a program reaches, so a method
        // nothing calls may hold what cannot compile. In a lazily prepared module its body is held until a reference; it
        // had been lowered with every other method, and its `@compileError` failed the build. zig returns 42.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                const b: lib.Box(u8) = .{ .value = 42 };
                return b.get();
            }
            """, ("lib.zig", """
            pub fn Box(comptime T: type) type {
                return struct {
                    value: T,
                    const Self = @This();
                    pub fn get(self: Self) T {
                        return self.value;
                    }
                    pub fn broken(self: Self) void {
                        _ = self;
                        @compileError("Box.broken is not meant to be called");
                    }
                };
            }
            """));
        cs.ShouldContain("_get(");
        cs.ShouldNotContain("_broken");
    }

    [Fact]
    public void A_called_method_of_a_reified_generic_still_raises_its_compile_error()
    {
        // Task #190: the held body lowers once referenced, so its diagnostic is zig's, at the call.
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                const b: lib.Box(u8) = .{ .value = 42 };
                b.broken();
                return b.get();
            }
            """, ("lib.zig", """
            pub fn Box(comptime T: type) type {
                return struct {
                    value: T,
                    const Self = @This();
                    pub fn get(self: Self) T {
                        return self.value;
                    }
                    pub fn broken(self: Self) void {
                        _ = self;
                        @compileError("Box.broken is not meant to be called");
                    }
                };
            }
            """)));
        ex.Message.ShouldContain("Box.broken is not meant to be called");
    }
}

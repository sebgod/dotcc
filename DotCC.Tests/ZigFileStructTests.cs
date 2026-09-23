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

    [Fact]
    public void A_generic_function_called_through_a_file_struct_type_is_a_loud_cut()
    {
        // `w.print(fmt, args)` is the next brick. Binding the template's placeholder signature would
        // report a bogus arity; the cut names the construct instead.
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const Box = @import("Box.zig");
            pub fn main() u8 {
                var b: Box = .init(40);
                b.addAny(@as(u8, 2));
                return b.value;
            }
            """, ("Box.zig", Box + "\npub fn addAny(b: *Box, n: anytype) void {\n    b.value += n;\n}\n")));
        ex.Message.ShouldContain("called through its file-as-struct type");
    }
}

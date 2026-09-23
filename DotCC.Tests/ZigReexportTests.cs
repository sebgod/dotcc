#nullable enable

using System;
using System.IO;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for RE-EXPORTED declarations (road-to-zig-std G3/G5): a top-level
/// <c>const NAME = name;</c> or <c>const NAME = mod.name;</c> names a declaration made elsewhere, and a
/// lookup of NAME follows it to the module that owns it. std re-exports 633 times in the pin:
/// <c>pub const indexOfScalar = findScalar;</c> (<c>mem.zig</c>), <c>pub const AutoHashMap =
/// hash_map.AutoHashMap;</c> (<c>std.zig</c>). End-to-end in the <c>import_reexports</c> zig-oracle program.
/// </summary>
[Collection("ZigFrontend")]
public sealed class ZigReexportTests
{
    private static string EmitZigMulti(string mainSource, params (string name, string source)[] siblings)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-zigrx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var mainPath = Path.Combine(dir, "main.zig");
        File.WriteAllText(mainPath, mainSource);
        foreach (var (name, source) in siblings) { File.WriteAllText(Path.Combine(dir, name), source); }
        try { return Compiler.EmitCSharp(new[] { mainPath }); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Inner = """
        pub fn add(a: u8, b: u8) u8 {
            return a + b;
        }
        pub fn maxOf(comptime T: type, a: T, b: T) T {
            return if (a > b) a else b;
        }
        pub fn Box(comptime T: type) type {
            return struct { v: T };
        }
        pub const Pair = struct { a: u8, b: u8 };
        fn double(x: u8) u8 {
            return x + x;
        }
        pub const twice = double;
        """;

    private const string Lib = """
        const inner = @import("inner.zig");
        pub const add = inner.add;
        pub const maxOf = inner.maxOf;
        pub const Box = inner.Box;
        pub const Pair = inner.Pair;
        pub const twice = inner.twice;
        """;

    [Fact]
    public void A_function_re_export_reaches_the_module_that_declares_it()
    {
        // Two hops for `twice` (lib → inner's alias → inner's `double`), one for `add`. Each lowers in
        // `inner`, under its prefix.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                return lib.add(lib.twice(10), 22);
            }
            """, ("lib.zig", Lib), ("inner.zig", Inner));
        cs.ShouldContain("inner__add(inner__double(10), 22)");
        cs.ShouldContain("static unsafe byte inner__double(byte x)");
    }

    [Fact]
    public void A_generic_re_export_instantiates_in_its_own_module()
    {
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            pub fn main() u8 {
                return lib.maxOf(u8, 42, 7);
            }
            """, ("lib.zig", Lib), ("inner.zig", Inner));
        cs.ShouldContain("inner__maxOf__u8(42, 7)");
    }

    [Fact]
    public void A_type_re_export_resolves_in_a_type_position()
    {
        // A container (`lib.Pair`, and a root alias of it) and a type-returning generic (`lib.Box(u8)`),
        // both declared in `inner`.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            const P = lib.Pair;
            pub fn main() u8 {
                const p: P = .{ .a = 30, .b = 10 };
                const q: lib.Pair = .{ .a = 1, .b = 0 };
                const b: lib.Box(u8) = .{ .v = 1 };
                return p.a + p.b + q.a + b.v;
            }
            """, ("lib.zig", Lib), ("inner.zig", Inner));
        cs.ShouldContain("inner__Pair p = new inner__Pair {");
        cs.ShouldContain("inner__Pair q = new inner__Pair {");
        cs.ShouldContain("inner__Box__u8 b =");
    }

    [Fact]
    public void A_root_alias_of_an_imported_function_is_a_call_not_a_global()
    {
        // `const add = lib.add;` has no runtime value: the root's global pass skips it, and a bare call
        // follows it into `inner`.
        var cs = EmitZigMulti("""
            const lib = @import("lib.zig");
            const add = lib.add;
            pub fn main() u8 {
                return add(40, 2);
            }
            """, ("lib.zig", Lib), ("inner.zig", Inner));
        cs.ShouldContain("return inner__add(40, 2);");
    }

    [Fact]
    public void A_referenced_declaration_that_did_not_parse_names_the_parse_error()
    {
        // An imported module is parsed resiliently: a declaration that does not parse is skipped so the
        // rest of the file stays usable. Referencing it used to read "call to unresolved name", which
        // sent the search the wrong way (std's findScalarPos, parseIntWithSign). A reference to it now
        // raises the parse error; the module's other declarations still work.
        const string util = "pub fn ok() u8 {\n    return 42;\n}\npub fn broken() u8 {\n    return 1 +;\n}\n";
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
                return util.broken();
            }
            """, ("util.zig", util)));
        ex.Message.ShouldContain("zig `broken` in util.zig did not parse");
        ex.Message.ShouldContain("line 5");
    }

    [Fact]
    public void A_re_export_cycle_is_an_error_not_a_hang()
    {
        var ex = Should.Throw<Exception>(() => EmitZigMulti("""
            const util = @import("util.zig");
            pub fn main() u8 {
                return util.a();
            }
            """, ("util.zig", "pub const a = b;\npub const b = a;\n")));
        ex.Message.ShouldContain("has no exported function 'a'");
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Building a code base of many units with the wat target, as CPython's wasm build does
/// (<c>examples/cpython/build-wat.sh</c>, GH #264): a unit compiled on its own
/// (<see cref="Compiler.EmitWatUnit"/>), each unit's own flags in one program
/// (<see cref="UnitFlags"/>), and what a whole program may leave undefined.
/// </summary>
[Collection("WatBackend")]
public sealed class WatWholeProgramTests
{
    /// <summary>Write each source to a temp file of its own, run <paramref name="compile"/> over
    /// the paths, and delete them.</summary>
    private static T WithSources<T>(Func<string[], T> compile, params string[] sources)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-wp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var paths = sources.Select((text, i) =>
            {
                var path = Path.Combine(dir, $"u{i}.c");
                File.WriteAllText(path, text);
                return path;
            }).ToArray();
            return compile(paths);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_header_struct_with_an_anonymous_union_is_one_type_in_every_unit()
    {
        // Every unit that includes the header defines the struct again; the first definition's
        // anonymous member stands, and a designator in a later unit still reaches its members.
        const string header = "struct obj { union { long rc; unsigned split[2]; }; int *type; };\n";
        string[] sources =
        [
            header + "int f1(struct obj *o) { return (int)o->rc; }\n",
            header + "static struct obj g = { .rc = 5, .type = 0 };\nint main(void) { return (int)g.rc; }\n",
        ];
        WithSources(paths => Compiler.EmitWat(paths), sources).ShouldContain("(func $main");
        WithSources(paths => Compiler.EmitCSharp(paths), sources).ShouldContain("g.__anon___Anon0.rc");
    }

    [Fact]
    public void A_unit_on_its_own_imports_what_it_does_not_define()
    {
        // --target=wat --emit=obj: a function the unit calls, one whose address it takes, and an
        // extern object it reads come from env instead of being refused.
        var wat = WithSources(paths => Compiler.EmitWatUnit(paths[0]),
            "extern int counter;\nint helper(int);\nint other(int);\nint (*fp)(int) = other;\nint g(int x) { return helper(x) + counter; }\n");
        wat.ShouldContain("(import \"env\" \"helper\" (func $helper (param i32) (result i32)))");
        wat.ShouldContain("(import \"env\" \"other\" (func $other (param i32) (result i32)))");
        wat.ShouldContain("(import \"env\" \"&counter\" (global $__addr_counter i32))");
        wat.ShouldContain("global.get $__addr_counter");
    }

    [Fact]
    public void Each_unit_is_preprocessed_with_its_own_flags()
    {
        // A compilation database's entries: one unit sees -DWHICH=1, the other -DWHICH=2 and
        // an include directory of its own.
        var dir = Path.Combine(Path.GetTempPath(), $"dotcc-wpf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "inc"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "inc", "extra.h"), "#define EXTRA 40\n");
            var a = Path.Combine(dir, "a.c");
            var b = Path.Combine(dir, "b.c");
            File.WriteAllText(a, "int one(void) { return WHICH; }\n");
            File.WriteAllText(b, "#include \"extra.h\"\nint one(void);\nint main(void) { return one() + WHICH + EXTRA; }\n");
            var units = new Dictionary<string, UnitFlags>
            {
                [a] = new(["WHICH=1"], []),
                [b] = new(["WHICH=2"], [Path.Combine(dir, "inc")]),
            };
            // b.c's quoted include is found through its own -I only: its directory has no extra.h.
            var wat = Compiler.EmitWat([a, b], units: units);
            wat.ShouldContain("i32.const 1");
            wat.ShouldContain("i32.const 2");
            wat.ShouldContain("i32.const 40");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_undefined_function_only_main_cannot_reach_is_no_error()
    {
        // As a linker with dead-code removal sees it: a function nothing calls may name what the
        // build leaves out; one main reaches may not, and every such name is reported at once.
        WithSources(paths => Compiler.EmitWat(paths),
            "int missing(int);\nstatic int unused(int x) { return missing(x); }\nint main(void) { return 0; }\n")
            .ShouldNotContain("missing");
        var ex = Should.Throw<CompileException>(() => WithSources(paths => Compiler.EmitWat(paths),
            "int missing(int);\nint gone(void);\nint main(void) { return missing(1) + gone(); }\n"));
        ex.Message.ShouldContain("call to 'missing'");
        ex.Message.ShouldContain("call to 'gone'");
    }

    [Fact]
    public void A_function_with_many_labels_dispatches_in_two_levels()
    {
        // Past 256 CFG blocks the goto dispatch picks a group, then a block within it, so the
        // blocks nest about 2*sqrt(n) deep: wabt overflows its stack between 500 and 2000.
        var src = new StringBuilder("int f(int x) {\n  int s = 0;\n");
        for (var i = 0; i < 400; i++) { src.Append($"  L{i}: s += {i}; if (s > x) goto L{(i * 7) % 400};\n"); }
        src.Append("  return s;\n}\nint main(void) { return f(5); }\n");
        var wat = WithSources(paths => Compiler.EmitWat(paths), src.ToString());
        wat.ShouldContain("block $cfg0");
        wat.ShouldContain("i32.div_u");
        var depth = 0;
        var maxDepth = 0;
        foreach (var line in wat.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("block ", StringComparison.Ordinal) || line.StartsWith("loop", StringComparison.Ordinal)) { maxDepth = Math.Max(maxDepth, ++depth); }
            else if (line == "end") { depth--; }
        }
        maxDepth.ShouldBeLessThan(100);
    }

    [Fact]
    public void A_compound_literal_in_a_static_initializer_has_static_storage()
    {
        // CPython's parser.c: an array of pointers to array compound literals at file scope.
        var wat = WithSources(paths => Compiler.EmitWat(paths),
            "typedef struct { const char *s; int n; } KW;\nstatic KW *table[] = { (KW[]) {{0, -1}}, (KW[]) {{\"if\", 1}, {0, -1}} };\nint main(void) { return table[1][0].n; }\n");
        wat.ShouldContain("(func $__init_globals");
    }
}

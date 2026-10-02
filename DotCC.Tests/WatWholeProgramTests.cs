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
}

#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for block-scope (local) AGGREGATE TYPE DEFINITIONS — a
/// struct/union/enum defined inside a function body, used as a statement
/// (`struct cD { … };`). C allows this; a type has no storage, so dotcc hoists
/// the definition into the top-level type section (deduped by tag) and the
/// statement emits nothing — IDENTICAL to its file-scope counterpart. Motivated
/// by Lua lstrlib's local alignment probe. End-to-end in `local-type-def/`.
/// </summary>
public sealed class LocalTypeDefTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ltd-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void block_scope_struct_def_hoists_to_top_level()
    {
        var src = WriteTemp("""
            int main(void) {
                struct cD { char c; double d; };
                struct cD x;
                x.c = 'z';
                x.d = 2.5;
                return (int)sizeof(struct cD);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // the type is hoisted (lives in the top-level type-decls section)
            emitted.ShouldContain("unsafe struct cD");
            // the definition statement itself emits nothing — there's no inline
            // `struct cD {` left in the method body
            emitted.ShouldContain("cD x = default;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void block_scope_enum_def_lowers_to_csharp_enum()
    {
        var src = WriteTemp("""
            int main(void) {
                enum local_e { LA, LB = 5, LC };
                return LA + LB + LC;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("enum local_e : int");
            emitted.ShouldContain("LB = 5");
            // enumerators resolve to the C# enum members
            emitted.ShouldContain("local_e.LA");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void block_scope_union_def_lowers_to_explicit_layout()
    {
        var src = WriteTemp("""
            int main(void) {
                union local_u { int i; char c[4]; };
                union local_u u;
                u.i = 1;
                return u.i;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("LayoutKind.Explicit");
            emitted.ShouldContain("unsafe struct local_u");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void block_scope_struct_with_nested_anon_union_member()
    {
        // Lua lstrlib's exact shape: a local struct whose member is an
        // anonymous union (the alignment probe).
        var src = WriteTemp("""
            int main(void) {
                struct cD { char c; union { double d; long l; void *p; } u; };
                return (int)sizeof(struct cD);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct cD");
        }
        finally { File.Delete(src); }
    }
}

#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the union form of typedef-with-body: <c>typedef union Tag { … }
/// Alias;</c> and the anonymous <c>typedef union { … } Alias;</c>. Both emit a
/// <c>[StructLayout(Explicit)]</c> + <c>[FieldOffset(0)]</c> struct (overlapping
/// storage), exactly like a plain <c>union</c> def. End-to-end behavior is in the
/// <c>typedef-union/</c> fixture.
/// </summary>
public sealed class TypedefUnionTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tu-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void tagged_typedef_union_emits_explicit_layout()
    {
        var src = WriteTemp("""
            typedef union Value { int i; float f; } Value;
            int main(void) { Value v; v.i = 1; return v.i; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("LayoutKind.Explicit");
            emitted.ShouldContain("FieldOffset(0)");
            emitted.ShouldContain("struct Value");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void anonymous_typedef_union_emits_under_alias()
    {
        var src = WriteTemp("""
            typedef union { long n; double d; } Num;
            int main(void) { Num m; m.d = 1.0; return (int)m.d; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("LayoutKind.Explicit");
            emitted.ShouldContain("struct Num");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void tagged_union_binds_tag_as_alias_when_different()
    {
        // `typedef union U_tag { … } U;` — the type emits under `U`, and `U_tag`
        // becomes a using-alias so `union U_tag` references still resolve.
        var src = WriteTemp("""
            typedef union U_tag { int a; int b; } U;
            int main(void) { union U_tag x; x.a = 7; return x.a; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("using unsafe U_tag = U;");
        }
        finally { File.Delete(src); }
    }
}

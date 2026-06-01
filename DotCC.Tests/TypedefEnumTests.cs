#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <c>typedef enum { … } Name;</c> (anonymous) and
/// <c>typedef enum Tag { … } Name;</c> (tagged) — the enum counterparts of
/// typedef-struct/union. Both lower to a real C# <c>enum Name : int { … }</c>.
/// End-to-end in the <c>typedef-enum/</c> fixture.
/// </summary>
public sealed class TypedefEnumTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tde-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void anonymous_typedef_enum_emits_real_enum_under_alias()
    {
        var src = WriteTemp("""
            typedef enum { A, B, C } E;
            int main(void) { E e = B; return (int)e; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("enum E : int");
            emitted.ShouldContain("B = 1");
            // bare enumerator resolves to Alias.Member
            emitted.ShouldContain("E.B");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void tagged_typedef_enum_binds_tag_alias()
    {
        var src = WriteTemp("""
            typedef enum Color { RED, GREEN = 5, BLUE } Color;
            int main(void) { enum Color c = BLUE; return (int)c; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("enum Color : int");
            emitted.ShouldContain("BLUE = 6");   // GREEN=5 → BLUE=6
        }
        finally { File.Delete(src); }
    }
}

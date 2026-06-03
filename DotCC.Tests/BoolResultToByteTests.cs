#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for storing a comparison / logical (<c>&amp;&amp;</c> / <c>||</c> /
/// <c>!</c>) result — C <c>int</c> 0/1 (lowered to a <c>CBool</c>-wrapped
/// expression) — into a NARROWER integer (a <c>lu_byte</c> field/local). C# has
/// no implicit <c>CBool</c>→<c>byte</c> conversion, so dotcc tags these results
/// <c>int</c>; the store-conversion layer then inserts the <c>(byte)</c> cast
/// (which chains <c>CBool</c>→int→byte). End-to-end in <c>bool-result-to-byte/</c>.
/// </summary>
public sealed class BoolResultToByteTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-brb-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void comparison_stored_into_byte_takes_a_cast()
    {
        var emitted = Emit("""
            typedef unsigned char lu_byte;
            int main(void) { int x = 5, y = 3; lu_byte b; b = x > y; return b; }
            """);
        emitted.ShouldContain("(lu_byte)(((CBool)");
    }

    [Fact]
    public void logical_and_stored_into_byte_takes_a_cast()
    {
        var emitted = Emit("""
            typedef unsigned char lu_byte;
            int main(void) { int x = 5, y = 3; lu_byte b; b = x && y; return b; }
            """);
        emitted.ShouldContain("(lu_byte)(((CBool)");
    }
}

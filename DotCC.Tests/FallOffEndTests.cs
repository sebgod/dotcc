#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// C lets a non-void function fall off its end: only a caller that uses the value is
/// undefined (C11 6.9.1p12). C# requires a return on every path (CS0161), so a body whose
/// end is reachable gets a trailing <c>return default;</c>; one that already ends in a
/// return gets none. CPython's pattern is a <c>switch</c> that returns in every case, then a
/// <c>Py_UNREACHABLE()</c> that is a plain call.
/// </summary>
public sealed class FallOffEndTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-foe-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_body_whose_end_is_reachable_returns_default()
    {
        var emitted = Emit("""
            void fatal(const char *m);
            static int name_of(int k) { switch (k) { case 0: return 10; case 1: return 11; } fatal("unreachable"); }
            int main(void) { return name_of(1); }
            """);
        emitted.ShouldContain("fatal(Libc.L(\"unreachable\\0\"u8));\n        return default(int);\n    }");
    }

    [Fact]
    public void A_body_ending_in_a_return_gets_no_second_one()
    {
        var emitted = Emit("""
            static int twice(int k) { return k * 2; }
            int main(void) { return twice(2); }
            """);
        emitted.ShouldNotContain("return default(int);");
    }
}

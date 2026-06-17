#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C's null pointer constant — an integer <c>0</c> used where a
/// pointer is expected. C# won't implicitly convert <c>int</c> 0 to a pointer, so
/// dotcc's store/return/argument coercion emits <c>null</c> when the target type
/// is a pointer (and the value is a constant 0). A non-pointer target is
/// unaffected. End-to-end in the <c>null-pointer-constant/</c> fixture.
/// </summary>
[Collection("NullPointerConstant")]
public sealed class NullPointerConstantTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-np-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void return_zero_from_pointer_function_emits_null()
    {
        var emitted = Emit("""
            int *f(int x) { if (x) return 0; return 0; }
            int main(void) { return f(1) ? 1 : 0; }
            """);
        // Both `return 0;` in the pointer-returning f become `return null;`.
        emitted.ShouldContain("return null;");
    }

    [Fact]
    public void zero_argument_to_pointer_param_emits_null()
    {
        var emitted = Emit("""
            int g(char *s) { return s ? 1 : 0; }
            int main(void) { return g(0); }
            """);
        emitted.ShouldContain("g(null)");
    }

    [Fact]
    public void zero_to_a_non_pointer_target_stays_zero()
    {
        // Guard: an int target keeps the literal 0 (no spurious null).
        var emitted = Emit("""
            int main(void) { int x = 0; return x; }
            """);
        emitted.ShouldContain("int x = 0;");
    }
}

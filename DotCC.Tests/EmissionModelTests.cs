#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// dotcc emits user functions (in exe / whole-program mode) as STATIC METHODS of
/// a <c>DotCcProgram</c> class, not as top-level local functions. Class methods
/// can be addressed (<c>&amp;fn</c>), stored in function-pointer tables, and
/// referenced from file-scope initializers and across contexts — none of which a
/// top-level local function supports (CS8801/CS8422/CS8787). The entry's
/// <c>main(...)</c> call and file-scope <c>&amp;fn</c> resolve by bare name via
/// <c>using static DotCcProgram;</c>. End-to-end in <c>fnptr-table/</c>.
/// </summary>
[Collection("EmissionModel")]
public sealed class EmissionModelTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-em-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void user_functions_are_static_methods_of_a_class()
    {
        var emitted = Emit("int add(int a, int b) { return a + b; } int main(void) { return add(1, 2); }");
        emitted.ShouldContain("static class DotCcProgram");
        emitted.ShouldContain("using static DotCcProgram;");
        emitted.ShouldContain("internal static int add(int a, int b)");
    }

    [Fact]
    public void functions_past_the_per_class_limit_continue_in_another_class()
    {
        // The CLR caps a type's methods (CPython's ~70k functions failed to load as one class):
        // past FunctionsPerClass the functions continue in DotCcProgram2, surfaced alike.
        var n = Compiler.FunctionsPerClass + 1;
        var src = new System.Text.StringBuilder();
        for (var i = 0; i < n; i++) { src.Append($"static int f{i}(void) {{ return {i % 7}; }}\n"); }
        src.Append($"int main(void) {{ return f0() + f{n - 1}(); }}\n");
        var emitted = Emit(src.ToString());
        emitted.ShouldContain("using static DotCcProgram;\nusing static DotCcProgram2;");
        emitted.ShouldContain("static class DotCcProgram2\n{\n    internal static int f8192(void)".Replace("(void)", "()"));
        emitted.ShouldNotContain("class DotCcProgram3");
    }

    [Fact]
    public void address_of_user_function_in_file_scope_initializer()
    {
        // `&fn` in a file-scope initializer resolves across the class boundary —
        // impossible if functions were top-level local functions.
        var emitted = Emit("""
            typedef int (*op)(int);
            static int neg(int x) { return -x; }
            static op g = &neg;
            int main(void) { return g(5); }
            """);
        emitted.ShouldContain("internal static int neg(int x)");
        emitted.ShouldContain("&neg");
    }

    [Fact]
    public void mutual_recursion_resolves_as_class_methods()
    {
        // Forward reference across functions — class methods resolve regardless of
        // order (a top-level local must be declared before use).
        var emitted = Emit("""
            int odd(int n);
            int even(int n) { return n == 0 ? 1 : odd(n - 1); }
            int odd(int n) { return n == 0 ? 0 : even(n - 1); }
            int main(void) { return even(10); }
            """);
        emitted.ShouldContain("internal static int even(int n)");
        emitted.ShouldContain("internal static int odd(int n)");
    }
}

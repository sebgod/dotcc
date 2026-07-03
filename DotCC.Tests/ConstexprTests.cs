#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for C23 `constexpr` object declarations — the initializer is
/// folded via <c>ConstEval</c> and bound onto the symbol
/// (<c>Symbol.ConstValue</c>), so the name resolves in every integer-constant-
/// expression position (array bounds, case labels, <c>_Static_assert</c>,
/// bit-field widths, <c>_Generic</c>). The emitted C# keeps an ordinary field/
/// local (reads and <c>&amp;</c> work; runtime is identical); the symbol is
/// const-qualified, so writes hit the standard read-only error. Diagnostics are
/// gcc-verbatim. V1 is the integer family; float/pointer/struct/array constexpr
/// is a loud unsupported cut. End-to-end in the `c23-constexpr/` fixture.
/// </summary>
[Collection("Constexpr")]
public sealed class ConstexprTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-constexpr-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string src, string std = "c23") =>
        Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std));

    [Fact]
    public void Constexpr_folds_into_integer_constant_expression_positions()
    {
        // A file-scope constexpr and one that references it: both fold, and the
        // value drives an array bound, a _Static_assert, a case label, and a
        // _Generic selection.
        var src = WriteTemp("""
            constexpr int N = 4;
            constexpr int M = N * 3 + 1;
            int main(void) {
                constexpr int local = 7;
                int arr[N];
                _Static_assert(M == 13, "M");
                switch (local) { case 7: break; default: return 99; }
                return (int)sizeof(arr) + _Generic(N, int: 0, default: 1);
            }
            """);
        try
        {
            var emitted = Emit(src);
            // N folded into the stackalloc extent (arrays lower to pointers).
            emitted.ShouldContain("stackalloc int[4]");
            // The global constexpr emits as a plain field (NOT C# `const`), so
            // taking its address stays legal.
            emitted.ShouldContain("int N = 4;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Writing_to_a_constexpr_object_is_a_read_only_error()
    {
        var src = WriteTemp("constexpr int k = 5; int main(void) { k = 6; return k; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("assignment of read-only variable 'k'");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Non_constant_initializer_is_rejected()
    {
        var src = WriteTemp("int f(void); constexpr int k = f(); int main(void) { return k; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("initializer element is not constant");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Missing_initializer_is_rejected()
    {
        var src = WriteTemp("constexpr int k; int main(void) { return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("'constexpr' requires an initialized data declaration");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Value_not_representable_in_the_declared_type_is_rejected()
    {
        var src = WriteTemp("constexpr unsigned char b = 300; int main(void) { return b; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("'constexpr' initializer not representable in type of object");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Float_constexpr_is_a_loud_unsupported_cut()
    {
        var src = WriteTemp("constexpr double d = 1.5; int main(void) { return (int)d; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("only integer constexpr objects are supported");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Constexpr_with_thread_local_is_rejected()
    {
        var src = WriteTemp("_Thread_local constexpr int k = 0; int main(void) { return k; }");
        try
        {
            Should.Throw<CompileException>(() => Emit(src))
                .Message.ShouldContain("'constexpr' may not be used with '_Thread_local'");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Constexpr_stays_an_ordinary_identifier_before_c23()
    {
        // Pre-C23 the spelling is NOT a keyword (no rule-2 promotion), so it is a
        // valid ordinary identifier — the same source keeps compiling under c17.
        var src = WriteTemp("int main(void) { int constexpr = 5; return constexpr - 5; }");
        try
        {
            Should.NotThrow(() => Emit(src, "c17"));
        }
        finally { File.Delete(src); }
    }
}

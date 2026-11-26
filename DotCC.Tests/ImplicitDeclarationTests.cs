#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A call to an undeclared function: C90's implicit declaration, removed in C99 (6.5.1p2). An
/// error from <c>-std=c99</c> on, as gcc 14 and clang 16 make it, and a warning under
/// <c>-std=c90</c>; the builtins dotcc recognizes by name need no declaration. dotcc used to emit
/// such a call, which then failed at the C# stage (or, typed <c>int</c>, truncated a double).
/// </summary>
[Collection("Attributes")]
public sealed class ImplicitDeclarationTests
{
    private static string Write(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-implicit-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void A_call_to_an_undeclared_function_is_an_error_from_c99_on()
    {
        var src = Write("int main(void) { return helper(2); }\nint helper(int x) { return x - 2; }\n");
        try
        {
            foreach (var std in new[] { "c99", "c11", "c17", "c23" })
            {
                Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std)))
                    .Message.ShouldContain("implicit declaration of function 'helper'");
            }
            // C90 allows it (a warning), and the later definition makes the call good.
            Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse("c90")).ShouldContain("return helper(2);");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void A_libc_function_needs_its_header()
    {
        var src = Write("int main(void) { return (int)fma(1.0, 2.0, 3.0) - 5; }\n");
        var withHeader = Write("#include <math.h>\nint main(void) { return (int)fma(1.0, 2.0, 3.0) - 5; }\n");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("implicit declaration of function 'fma'");
            Compiler.EmitCSharp(new[] { withHeader }).ShouldContain("fma(1.0, 2.0, 3.0)");
        }
        finally { File.Delete(src); File.Delete(withHeader); }
    }

    [Fact]
    public void Builtins_recognized_by_name_need_no_declaration()
    {
        var src = Write("""
            #include <stdarg.h>
            #include <stdatomic.h>
            static int sum(int n, ...) {
                va_list ap; va_start(ap, n);
                int s = 0; for (int i = 0; i < n; i++) s += va_arg(ap, int);
                va_end(ap); return s;
            }
            int main(void) {
                atomic_int a = 1;
                int b = 2;
                atomic_fetch_add(&a, 1);
                __atomic_fetch_add(&b, 1, __ATOMIC_SEQ_CST);
                return sum(2, atomic_load(&a), __atomic_load_n(&b, __ATOMIC_RELAXED)) - 5;
            }
            """);
        try
        {
            Should.NotThrow(() => Compiler.EmitCSharp(new[] { src }));
        }
        finally { File.Delete(src); }
    }
}

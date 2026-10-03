#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #223: a function declarator is an init-declarator (C11 6.7.6.3), so a
/// prototype is an ordinary declaration: a list may declare several functions and
/// objects, a typedef may name a function TYPE (CPython's <c>typedef int
/// _Py_once_fn_t(void *arg);</c>, used as <c>_Py_once_fn_t *fn</c>), and a function
/// may be declared at block scope, plain or <c>extern</c> (pythonrun.c). A pointer
/// declarator over a function type is the fn-ptr type. <c>(void)</c> means no
/// parameters everywhere (C11 6.7.6.3p10). End-to-end in
/// <c>function-declarators/</c> (gcc-matched).
/// </summary>
[Collection("FunctionDeclarator")]
public sealed class FunctionDeclaratorTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fndecl-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fndecl-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path })).Message; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void One_declaration_declares_several_functions_and_an_object()
    {
        var emitted = Emit("""
            int twice(int), (thrice)(int), total = 3;
            int main(void) { return twice(total) + thrice(1); }
            int twice(int x) { return 2 * x; }
            int (thrice)(int x) { return 3 * x; }
            """);
        emitted.ShouldContain("public static unsafe int total = 3;");
        emitted.ShouldContain("return twice(total) + thrice(1);");
        emitted.ShouldContain("static int thrice(int x)");
    }

    [Fact]
    public void A_function_type_typedef_names_the_type_a_pointer_declarator_points_at()
    {
        var emitted = Emit("""
            typedef int once_fn_t(void *arg);
            static int apply(once_fn_t *fn, void *arg) { return fn(arg); }
            static int one(void *arg) { return arg != 0; }
            int main(void) { once_fn_t *f = one; return apply(f, 0); }
            """);
        emitted.ShouldContain("int apply(delegate*<void*, int> fn, void* arg)");
        emitted.ShouldContain("delegate*<void*, int> f = &one;");
    }

    [Fact]
    public void A_parameter_of_a_function_typedef_type_is_a_function_pointer()
    {
        var emitted = Emit("""
            typedef int binop_t(int, int);
            static int combine(binop_t op, int a, int b) { return op(a, b); }
            static int add(int a, int b) { return a + b; }
            int main(void) { return combine(add, 1, 2); }
            """);
        emitted.ShouldContain("int combine(delegate*<int, int, int> op, int a, int b)");
    }

    [Fact]
    public void A_block_scope_prototype_declares_a_later_function()
    {
        var emitted = Emit("""
            int main(void) { int helper(int), unused = 0; extern int twice(int); return helper(1) + twice(unused); }
            int helper(int x) { return x + 1; }
            int twice(int x) { return 2 * x; }
            """);
        emitted.ShouldContain("return helper(1) + twice(unused);");
        emitted.ShouldContain("int unused = 0;");
    }

    [Fact]
    public void A_block_scope_extern_object_refers_to_the_file_scope_definition()
    {
        var emitted = Emit("""
            static int read_total(void) { extern int total; return total; }
            int total = 42;
            int main(void) { return read_total(); }
            """);
        emitted.ShouldContain("return total;");
        emitted.ShouldContain("public static unsafe int total = 42;");
    }

    [Fact]
    public void A_prototype_of_a_function_returning_a_function_pointer()
    {
        var emitted = Emit("""
            static int twice(int x) { return 2 * x; }
            int (*pick(int which))(int);
            int main(void) { return pick(0)(3); }
            int (*pick(int which))(int) { return twice; }
            """);
        emitted.ShouldContain("delegate*<int, int> pick(int which)");
    }

    [Fact]
    public void A_void_parameter_list_has_no_parameters()
    {
        var emitted = Emit("""
            static int zero(void);
            static int zero(void) { return 0; }
            int main(void) { return zero(); }
            """);
        emitted.ShouldContain("static int zero()");
    }

    [Theory]
    [InlineData("int main(void) { static int f(void); return 0; }", "invalid storage class for function 'f'")]
    [InlineData("struct s { int f(void); }; int main(void) { return 0; }", "field 'f' declared as a function")]
    [InlineData("int main(void) { extern int x = 1; return x; }", "'x' has both 'extern' and initializer")]
    public void Invalid_function_declarations_are_rejected(string source, string message)
        => Rejected(source).ShouldContain(message);
}

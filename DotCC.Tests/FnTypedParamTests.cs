#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #220: a parameter declared with a function type is adjusted to a pointer
/// to that function (C11 6.7.6.3p8), in both spellings CPython uses
/// (<c>int siftup(PyListObject *, Py_ssize_t)</c>, <c>void *(func)(Parser *)</c>);
/// a pointer to a function pointer (<c>int (**func)(void *)</c>) is a declarator
/// name like <c>(*name)</c>; and <c>static int (name)(…)</c> is a parenthesized
/// function name. <c>&amp;f</c> of a function designator is the fn-ptr type, so a
/// <c>Pointer(Func)</c> is only ever a pointer to a function pointer, whose
/// <c>*</c> a call keeps. End-to-end in <c>fn-typed-params/</c> (gcc-matched).
/// </summary>
[Collection("FnTypedParam")]
public sealed class FnTypedParamTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fnparam-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Function_typed_parameters_are_function_pointers()
    {
        var emitted = Emit("""
            static int apply(int f(int), int v) { return f(v); }
            static void *run(void *(func)(char *), char *s) { return func(s); }
            static int id(int x) { return x; }
            int main(void) { return apply(id, 3); }
            """);
        emitted.ShouldContain("int apply(delegate*<int, int> f, int v)");
        emitted.ShouldContain("void* run(delegate*<byte*, void*> func, byte* s)");
        emitted.ShouldContain("return f(v);");
    }

    [Fact]
    public void A_pointer_to_a_function_pointer_keeps_its_dereference()
    {
        var emitted = Emit("""
            static int one(int x) { return x + 1; }
            static void set(int (**slot)(int)) { *slot = one; }
            int main(void) {
                int (*fp)(int) = one;
                int (**pp)(int) = &fp;
                set(pp);
                return (*pp)(1) + (**pp)(2);
            }
            """);
        emitted.ShouldContain("void set(delegate*<int, int>* slot)");
        emitted.ShouldContain("delegate*<int, int>* pp = &fp;");
        emitted.ShouldContain("(*pp)(1) + (*pp)(2)");
    }

    [Fact]
    public void Qualified_function_pointer_parameter()
    {
        var emitted = Emit("""
            static int call(int (*const f)(int), int v) { return f(v); }
            static int id(int x) { return x; }
            int main(void) { return call(id, 4); }
            """);
        emitted.ShouldContain("int call(delegate*<int, int> f, int v)");
    }

    [Fact]
    public void Static_function_with_a_parenthesized_name()
    {
        var emitted = Emit("""
            static int (named)(int x) { return x + 1; }
            int main(void) { return named(1); }
            """);
        emitted.ShouldContain("static unsafe int named(int x)");
    }

    [Fact]
    public void Calling_a_pointer_to_a_function_pointer_directly_is_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fnparam-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, """
            static int one(int x) { return x; }
            int main(void) { int (*fp)(int) = one; int (**pp)(int) = &fp; return pp(1); }
            """);
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path }))
                .Message.ShouldContain("called object is not a function or function pointer");
        }
        finally { File.Delete(path); }
    }
}

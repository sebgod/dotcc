#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// const-correctness checking (clang-shaped, on by default). Writing through a
/// <c>const</c>-qualified lvalue — assignment or <c>++</c>/<c>--</c> — is a C
/// constraint violation, so dotcc rejects it with a <see cref="CompileException"/>
/// (gcc/clang parity: "assignment of read-only variable"). Initialization is not a
/// write, so a <c>const</c> declaration with an initializer compiles. Violations
/// inside system headers don't fire on the user. (The qualifier reaches the IR via
/// the Type prefix/postfix productions — see <see cref="ConstQualifierTests"/>.)
/// </summary>
public sealed class ConstCheckTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-constck-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void assigning_to_a_const_variable_is_an_error()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { const int x = 5; x = 6; return x; }
            """));
        ex.Message.ShouldContain("read-only");
        ex.Message.ShouldContain("x");
    }

    [Fact]
    public void incrementing_a_const_variable_is_an_error()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { const int x = 5; x++; return x; }
            """));
        ex.Message.ShouldContain("read-only");
    }

    [Fact]
    public void compound_assignment_to_a_const_variable_is_an_error()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { const int x = 5; x += 1; return x; }
            """));
        ex.Message.ShouldContain("read-only");
    }

    [Fact]
    public void writing_through_a_pointer_to_const_is_an_error()
    {
        // `*p = ...` where p is `const int *` — writing through a pointer-to-const.
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { int v = 0; const int *p = &v; *p = 7; return v; }
            """));
        ex.Message.ShouldContain("read-only");
    }

    [Fact]
    public void writing_a_const_array_element_is_an_error()
    {
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { const char s[] = "hi"; s[0] = 'H'; return s[0]; }
            """));
        ex.Message.ShouldContain("read-only");
    }

    [Fact]
    public void initializing_a_const_is_fine()
    {
        // Initialization is not a write — a const decl with an initializer compiles.
        var emit = Emit("""
            int main(void) { const int x = 5; return x; }
            """);
        emit.ShouldContain("int x = 5");
    }

    [Fact]
    public void repointing_a_const_pointer_is_an_error_but_writing_through_it_is_fine()
    {
        // `int * const p` — the POINTER is const (repointing errors), the pointee is
        // mutable (writing through it is fine). Re-pointing trips the check.
        var ex = Should.Throw<CompileException>(() => Emit("""
            int main(void) { int a = 1, b = 2; int * const p = &a; p = &b; return *p; }
            """));
        ex.Message.ShouldContain("read-only");
    }

    [Fact]
    public void writing_through_a_const_pointer_pointee_is_fine()
    {
        // `int * const p` — `*p = v` writes the mutable pointee, not the const pointer.
        var emit = Emit("""
            int main(void) { int a = 1; int * const p = &a; *p = 9; return a; }
            """);
        emit.ShouldContain("static unsafe int main");
    }

    [Fact]
    public void writing_a_plain_non_const_variable_still_works()
    {
        var emit = Emit("""
            int main(void) { int x = 5; x = 6; x++; return x; }
            """);
        emit.ShouldContain("static unsafe int main");
    }
}

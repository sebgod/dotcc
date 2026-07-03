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
[Collection("Console")]
public sealed class ConstCheckTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-constck-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    /// <summary>Emit and capture stderr (where const-discard warnings land). The
    /// unit assembly is serialized (AssemblyInfo), so the process-global
    /// <c>Console.Error</c> swap is race-free. <paramref name="warnDiscard"/> toggles
    /// the const-discard warning (the `-Wno-discarded-qualifiers` knob).</summary>
    private static (string Emit, string Stderr) EmitWithStderr(string body, bool warnDiscard = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-constck-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { return (Compiler.EmitCSharp(new[] { path }, warnings: warnDiscard ? WarningFlags.Default : WarningFlags.None), sw.ToString()); }
        finally { Console.SetError(prior); File.Delete(path); }
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

    // ---- discard-qualifier warnings (gcc -Wdiscarded-qualifiers) -----------

    [Fact]
    public void passing_a_pointer_to_const_to_a_non_const_param_warns()
    {
        var (_, stderr) = EmitWithStderr("""
            static int sink(char *p) { return p[0]; }
            int main(void) { const char *q = "x"; return sink(q); }
            """);
        stderr.ShouldContain("discards 'const' qualifier");
        stderr.ShouldContain("sink");
    }

    [Fact]
    public void passing_a_pointer_to_const_to_a_const_param_is_fine()
    {
        var (_, stderr) = EmitWithStderr("""
            static int sink(const char *p) { return p[0]; }
            int main(void) { const char *q = "x"; return sink(q); }
            """);
        stderr.ShouldNotContain("discards");
    }

    [Fact]
    public void an_explicit_cast_suppresses_the_discard_warning()
    {
        // Casting away const is the programmer's deliberate override — no warning.
        var (_, stderr) = EmitWithStderr("""
            static int sink(char *p) { return p[0]; }
            int main(void) { const char *q = "x"; return sink((char *)q); }
            """);
        stderr.ShouldNotContain("discards");
    }

    [Fact]
    public void passing_a_non_const_pointer_to_a_const_param_is_fine()
    {
        // Adding const is always allowed.
        var (_, stderr) = EmitWithStderr("""
            static int sink(const char *p) { return p[0]; }
            int main(void) { char buf[2] = { 'x', 0 }; return sink(buf); }
            """);
        stderr.ShouldNotContain("discards");
    }

    [Fact]
    public void assigning_a_pointer_to_const_to_a_plain_pointer_warns()
    {
        var (_, stderr) = EmitWithStderr("""
            int main(void) { const char *q = "x"; char *p; p = q; return p[0]; }
            """);
        stderr.ShouldContain("discards 'const' qualifier");
    }

    [Fact]
    public void initializing_a_plain_pointer_from_a_pointer_to_const_warns()
    {
        var (_, stderr) = EmitWithStderr("""
            int main(void) { const char *q = "x"; char *p = q; return p[0]; }
            """);
        stderr.ShouldContain("discards 'const' qualifier");
        stderr.ShouldContain("initialization");
    }

    [Fact]
    public void returning_a_pointer_to_const_through_a_plain_pointer_warns()
    {
        var (_, stderr) = EmitWithStderr("""
            static char *strip(const char *s) { return s; }
            int main(void) { return strip("x")[0]; }
            """);
        stderr.ShouldContain("discards 'const' qualifier");
        stderr.ShouldContain("return");
    }

    [Fact]
    public void wno_discarded_qualifiers_suppresses_the_warning_but_not_the_error()
    {
        // -Wno-discarded-qualifiers silences the discard warning…
        var (_, stderr) = EmitWithStderr("""
            static int sink(char *p) { return p[0]; }
            int main(void) { const char *q = "x"; return sink(q); }
            """, warnDiscard: false);
        stderr.ShouldNotContain("discards");

        // …but a write-to-const stays a hard error regardless of the flag.
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-constck-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, "int main(void) { const int x = 5; x = 6; return x; }");
        try
        {
            Should.Throw<CompileException>(() =>
                Compiler.EmitCSharp(new[] { path }, warnings: WarningFlags.None));
        }
        finally { File.Delete(path); }
    }
}

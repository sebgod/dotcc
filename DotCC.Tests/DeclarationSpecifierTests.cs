#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #223 / #245: the storage classes and function specifiers are one DeclSpec, a
/// Type prefix or suffix, so each composes with any type (a typedef name, a tag, a
/// qualifier: CPython's <c>inline const T f(…)</c>, <c>inline struct T f(…)</c>,
/// <c>_Thread_local const</c>, a <c>register</c> parameter) and they come in any
/// order (<c>inline static</c>, <c>const static int</c>, <c>int typedef T</c>). The
/// binder reads the storage class structurally and applies C's constraints with
/// gcc's wording. An empty declaration at file scope and an empty translation unit
/// are accepted (gcc's -pedantic warnings), and <c>return</c> takes a comma
/// expression. End-to-end in <c>declaration-specifiers/</c> and
/// <c>storage-class-order/</c> (gcc-matched).
/// </summary>
[Collection("Console")]
public sealed class DeclarationSpecifierTests
{
    private static string Emit(string body, CDialect? dialect = null, WarningFlags warnings = WarningFlags.Default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-declspec-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }, dialect: dialect, warnings: warnings); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body, CDialect? dialect = null, WarningFlags warnings = WarningFlags.Default)
        => Should.Throw<CompileException>(() => Emit(body, dialect, warnings)).Message;

    /// <summary>What compiling <paramref name="body"/> writes to stderr (its warnings).</summary>
    private static string Stderr(string body)
    {
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { Emit(body); return sw.ToString(); }
        finally { Console.SetError(prior); }
    }

    [Fact]
    public void Inline_composes_with_a_qualifier_a_tag_and_a_typedef_name()
    {
        var emitted = Emit("""
            typedef struct Pair { int a, b; } Pair;
            static Pair pairs[1] = { { 1, 2 } };
            static inline const Pair *pick(void) { return &pairs[0]; }
            static inline struct Pair *first(void) { return &pairs[0]; }
            inline Pair copy(void) { return pairs[0]; }
            int main(void) { return pick()->a + first()->b + copy().a; }
            """);
        emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal static unsafe Pair* pick()");
        emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal static unsafe Pair* first()");
        emitted.ShouldContain("static unsafe Pair copy()");
    }

    [Fact]
    public void Noreturn_composes_with_a_typedef_name()
    {
        var emitted = Emit("""
            typedef void nothing;
            _Noreturn nothing stop(void) { for (;;) {} }
            int main(void) { return 0; }
            """);
        emitted.ShouldContain("[System.Diagnostics.CodeAnalysis.DoesNotReturn]");
    }

    [Fact]
    public void Thread_local_composes_with_const()
    {
        var emitted = Emit("""
            typedef struct S { int v; } S;
            static _Thread_local const S *current;
            int main(void) { return current != 0; }
            """);
        emitted.ShouldContain("[ThreadStatic]\n    public static unsafe S* current;");
    }

    [Fact]
    public void Register_parameters_and_locals_lower_as_plain_ones()
    {
        var emitted = Emit("""
            typedef unsigned long size_type;
            static size_type twice(register size_type n) { register size_type r = 2 * n; return r; }
            int main(void) { for (register int i = 0; i < 1; i++) {} return (int)twice(1); }
            """);
        emitted.ShouldContain("static unsafe ulong twice(ulong n)");
        emitted.ShouldContain("ulong r = (ulong)(2) * n;");
    }

    [Theory]
    [InlineData("register int g;\nint main(void) { return 0; }", "register name not specified for 'g'")]
    [InlineData("register int f(void) { return 0; }\nint main(void) { return 0; }", "function definition declared 'register'")]
    [InlineData("int main(void) { register int x = 1; int *p = &x; return *p; }", "address of register variable 'x' requested")]
    [InlineData("int f(register int x) { return *&x; }\nint main(void) { return 0; }", "address of register variable 'x' requested")]
    [InlineData("int main(void) { register int f(void); return 0; }", "invalid storage class for function 'f'")]
    [InlineData("int main(void) { static register int x; return 0; }", "multiple storage classes in declaration specifiers")]
    [InlineData("typedef register int T;\nint main(void) { return 0; }", "multiple storage classes in declaration specifiers")]
    [InlineData("extern register int y;\nint main(void) { return 0; }", "multiple storage classes in declaration specifiers")]
    [InlineData("int main(void) { auto register int z = 0; return z; }", "multiple storage classes in declaration specifiers")]
    public void Register_constraints_are_gcc_errors(string source, string message)
        => Rejected(source).ShouldContain(message);

    [Fact]
    public void Storage_classes_and_function_specifiers_come_in_any_order()
    {
        var emitted = Emit("""
            int static counter;
            const static int step = 2;
            int typedef word;
            inline static int *counter_ptr(void) { counter += step; return &counter; }
            int extern later;
            int main(void) { int static calls = 0; word w = 1; calls++; return *counter_ptr() + w + calls + later; }
            int later = 7;
            """);
        emitted.ShouldContain("[MethodImpl(MethodImplOptions.AggressiveInlining)]\n    internal static unsafe int* counter_ptr()");
        emitted.ShouldContain("public static unsafe int calls__s0 = 0;");
        emitted.ShouldContain("int w = 1;");
    }

    [Fact]
    public void A_for_loop_may_declare_a_static_local_and_pedantic_diagnoses_it()
    {
        const string source = "int main(void) { int n = 0; for (static int i = 0; i < 2; i++) { n++; } return n; }";
        Emit(source).ShouldContain("for (; i__s0 < 2; i__s0++)");
        Rejected(source, warnings: WarningFlags.Default | WarningFlags.PedanticErrors)
            .ShouldContain("declaration of static variable 'i' in 'for' loop initial declaration");
    }

    [Theory]
    [InlineData("int main(void) { for (extern int i; ; ) { return 0; } }", "declaration of 'extern' variable 'i' in 'for' loop initial declaration")]
    [InlineData("int main(void) { for (typedef int T; ; ) { return 0; } }", "declaration of non-variable 'T' in 'for' loop initial declaration")]
    public void Pedantic_diagnoses_a_non_automatic_for_loop_declaration(string source, string message)
        => Rejected(source, warnings: WarningFlags.Default | WarningFlags.PedanticErrors).ShouldContain(message);

    [Theory]
    [InlineData("static extern int x;\nint main(void) { return 0; }", "multiple storage classes in declaration specifiers")]
    [InlineData("int main(void) { static int extern y; return 0; }", "multiple storage classes in declaration specifiers")]
    [InlineData("static static int x;\nint main(void) { return 0; }", "duplicate 'static'")]
    [InlineData("typedef _Thread_local int T;\nint main(void) { return 0; }", "'_Thread_local' used with 'typedef'")]
    [InlineData("int *static p;\nint main(void) { return 0; }", "expected identifier or '(' before 'static'")]
    [InlineData("int main(void) { return (int)(static int)3; }", "storage class specifier in cast")]
    [InlineData("int main(void) { return (int)sizeof(static int); }", "storage class specifier in 'sizeof'")]
    [InlineData("int main(void) { return (int)_Alignof(extern int); }", "storage class specifier in '_Alignof'")]
    [InlineData("int main(void) { return (int)sizeof(inline int); }", "expected expression before 'inline'")]
    [InlineData("struct S { static int x; };\nint main(void) { return 0; }", "expected specifier-qualifier-list before 'static'")]
    [InlineData("struct S { inline int x; };\nint main(void) { return 0; }", "expected specifier-qualifier-list before 'inline'")]
    [InlineData("void f(static int x) { }\nint main(void) { return 0; }", "storage class specified for parameter 'x'")]
    [InlineData("void f(typedef int x);\nint main(void) { return 0; }", "storage class specified for parameter 'x'")]
    [InlineData("int f(int (*)(extern int));\nint main(void) { return 0; }", "storage class specified for unnamed parameter")]
    [InlineData("auto int x;\nint main(void) { return 0; }", "file-scope declaration of 'x' specifies 'auto'")]
    [InlineData("auto int f(void);\nint main(void) { return 0; }", "file-scope declaration of 'f' specifies 'auto'")]
    [InlineData("typedef int f(void) { return 0; }\nint main(void) { return 0; }", "function definition declared 'typedef'")]
    [InlineData("int main(void) { static void g(void); return 0; }", "invalid storage class for function 'g'")]
    [InlineData("inline struct S { int a; };\nint main(void) { return 0; }", "'inline' in empty declaration")]
    public void Storage_class_constraints_are_gcc_errors(string source, string message)
        => Rejected(source).ShouldContain(message);

    [Theory]
    [InlineData("static struct S { int a; };\nint main(void) { return 0; }", "useless storage class specifier in empty declaration")]
    [InlineData("int main(void) { typedef struct S { int a; }; return 0; }", "useless storage class specifier in empty declaration")]
    [InlineData("auto int f(void) { return 0; }\nint main(void) { return f(); }", "function definition declared 'auto'")]
    [InlineData("void f(inline int x) { }\nint main(void) { return 0; }", "parameter 'x' declared 'inline'")]
    public void Meaningless_specifiers_are_gcc_warnings(string source, string warning)
        => Stderr(source).ShouldContain(warning);

    [Fact]
    public void A_stray_file_scope_semicolon_is_accepted_and_pedantic_diagnoses_it()
    {
        const string source = "#define NOTHING(x)\nNOTHING(a);\nint f(void) { return 1; };\nint main(void) { return f(); }\n";
        Emit(source).ShouldContain("return f();");
        Rejected(source, warnings: WarningFlags.Default | WarningFlags.PedanticErrors)
            .ShouldContain("ISO C does not allow extra ';' outside of a function (line 2)");
    }

    [Fact]
    public void An_empty_translation_unit_is_accepted_and_pedantic_diagnoses_it()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"dotcc-declspec-{Guid.NewGuid():N}.c");
        var main = Path.Combine(Path.GetTempPath(), $"dotcc-declspec-{Guid.NewGuid():N}.c");
        File.WriteAllText(empty, "#if 0\nint unused;\n#endif\n");
        File.WriteAllText(main, "int main(void) { return 0; }\n");
        try
        {
            Compiler.EmitCSharp(new[] { empty, main }).ShouldContain("int main()");
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { empty, main },
                    warnings: WarningFlags.Default | WarningFlags.PedanticErrors))
                .Message.ShouldContain("ISO C forbids an empty translation unit");
        }
        finally { File.Delete(empty); File.Delete(main); }
    }

    [Fact]
    public void Return_and_a_for_condition_take_a_comma_expression()
    {
        var emitted = Emit("""
            static int calls = 0;
            static int count(int v) { calls++; return v; }
            static int last(int a, int b) { return count(a), count(b); }
            int main(void) { int i; for (i = 0; i++, i < 3;) {} return last(i, calls); }
            """);
        emitted.ShouldContain("count(a);\n        return count(b);");
        emitted.ShouldContain("for (i = 0; ((i++, ((CBool)(i < 3))).Item2) != 0; )");
    }
}

#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #223: array declarators and brace-enclosed initializers are ordinary
/// init-declarators (C11 6.7.6, 6.7.9), so every declarator form takes part in
/// every declarator list at every scope: CPython's <c>char rawmode[6], *m;</c>, a
/// table whose outer bound its initializer sizes (<c>specs[][2]</c>), a
/// parenthesized array declarator (<c>PyObject *(values[N])</c>), braced scalars,
/// fn-ptr tables beside other declarators, and <c>static</c> locals that mix a
/// counter with a lookup table. End-to-end in <c>declarator-lists/</c> (gcc-matched).
/// </summary>
[Collection("DeclaratorList")]
public sealed class DeclaratorListTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-decls-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-decls-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path })).Message; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_array_declarator_comes_first_in_a_block_scope_list()
    {
        var emitted = Emit("""
            int main(void) { char rawmode[6], *m; m = rawmode; return m[0]; }
            """);
        emitted.ShouldContain("byte* rawmode = stackalloc byte[6];");
        emitted.ShouldContain("byte* m = default;");
    }

    [Fact]
    public void File_scope_lists_mix_scalars_arrays_and_pointers()
    {
        var emitted = Emit("""
            int counter = 3, table[4] = { 1, 2, 3, 4 }, *cursor;
            int main(void) { cursor = table + counter; return *cursor; }
            """);
        emitted.ShouldContain("public static int counter = 3;");
        emitted.ShouldContain("public static unsafe int* table = Libc.GlobalArrayFrom<int>(new int[]{ 1, 2, 3, 4 });");
        emitted.ShouldContain("public static unsafe int* cursor;");
    }

    [Fact]
    public void An_open_outer_bound_is_sized_by_the_initializer()
    {
        var emitted = Emit("""
            const char specs[][2] = { { 'a', 'b' }, { 'c', 'd' }, { 'e', 'f' } };
            int main(void) { return (int)sizeof specs; }
            """);
        emitted.ShouldContain("new byte[]{ 97, 98, 99, 100, 101, 102 }");
        emitted.ShouldContain("3 * (2 * sizeof(byte))");
    }

    [Fact]
    public void A_parenthesized_array_declarator_is_the_same_array()
    {
        var emitted = Emit("""
            int main(void) { const char *(values[3]) = { "x", "y", "z" }; return values[2][0]; }
            """);
        emitted.ShouldContain("byte** values = stackalloc byte*[]{ ");
    }

    [Fact]
    public void A_braced_scalar_initializer_is_its_value()
    {
        var emitted = Emit("""
            static int braced = { 42 };
            int main(void) { int n = { 5 }; return n + braced; }
            """);
        emitted.ShouldContain("int n = 5;");
        emitted.ShouldContain("public static int braced = 42;");
    }

    [Fact]
    public void A_static_local_list_mixes_a_counter_and_a_table()
    {
        var emitted = Emit("""
            static int bump(void) { static int calls = 0, seen[3]; seen[calls % 3]++; return ++calls; }
            int main(void) { return bump(); }
            """);
        emitted.ShouldContain("public static int calls__s0 = 0;");
        emitted.ShouldContain("public static unsafe int* seen__s1 = Libc.GlobalArrayZeroed<int>(3);");
    }

    [Fact]
    public void A_fn_ptr_table_sits_in_a_list_beside_a_fn_ptr()
    {
        var emitted = Emit("""
            static int twice(int x) { return 2 * x; }
            int (*ops[])(int) = { twice, twice }, (*chosen)(int) = twice;
            int main(void) { return ops[1](1) + chosen(2); }
            """);
        emitted.ShouldContain("public static unsafe delegate*<int, int>* ops = ");
        emitted.ShouldContain("public static unsafe delegate*<int, int> chosen = &twice;");
    }

    [Fact]
    public void An_extern_array_declaration_is_satisfied_by_a_later_definition()
    {
        var emitted = Emit("""
            extern const int primes[];
            const int primes[3] = { 2, 3, 5 }, count = 3;
            int main(void) { return primes[count - 1]; }
            """);
        emitted.ShouldContain("public static unsafe int* primes = ");
        emitted.ShouldContain("public static int count = 3;");
    }

    [Fact]
    public void Array_members_share_a_list_with_pointers_and_scalars()
    {
        var emitted = Emit("""
            struct mode { char rawmode[6], *m; int flags[2], n; };
            int main(void) { struct mode md; md.n = 1; return md.n; }
            """);
        emitted.ShouldContain("public unsafe fixed byte rawmode[6];");
        emitted.ShouldContain("public unsafe byte* m;");
        emitted.ShouldContain("public unsafe fixed int flags[2];");
    }

    [Theory]
    [InlineData("int main(void) { int a[2] = 5; return 0; }", "'a': an array initializer must be an initializer list or a string literal")]
    [InlineData("int main(void) { int a[]; return 0; }", "array size missing in 'a'")]
    [InlineData("int main(void) { int n = 2; int v[n] = { 1 }; return 0; }", "variable-sized object 'v' may not be initialized")]
    [InlineData("int main(void) { short w[] = \"abc\"; return 0; }", "'w': a string literal of this width cannot initialize an array of 'short'")]
    [InlineData("struct s { int a = 1; }; int main(void) { return 0; }", "struct or union member 'a' cannot have an initializer")]
    [InlineData("int n = 2; int g[n]; int main(void) { return 0; }", "storage size of 'g' isn't constant")]
    public void Invalid_declarators_are_rejected(string source, string message)
        => Rejected(source).ShouldContain(message);
}

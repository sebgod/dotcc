#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #333: the C grammar has C11 6.7.6's declarators (<c>Pointer? DirectDeclarator</c>,
/// a direct declarator being a name, a parenthesized declarator, or one followed by an
/// array bound or a parameter list) and 6.7.7's abstract ones, folded into a type
/// inside out by one binder function. So every nesting declares what C says it does,
/// in every position: a declaration, a member, a parameter, a function definition, a
/// type name. End-to-end in <c>declarators/</c> (gcc-matched).
/// </summary>
[Collection("DeclaratorList")]
public sealed class DeclaratorTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-declarator-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-declarator-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { path })).Message; }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Each_declarator_has_its_own_pointers()
    {
        // The `*` belongs to the declarator, not to the specifiers: only `a` and `c`
        // are pointers.
        var emitted = Emit("""
            int *a, b, **c;
            int main(void) { return b; }
            """);
        emitted.ShouldContain("public static unsafe int* a;");
        emitted.ShouldContain("public static int b;");
        emitted.ShouldContain("public static unsafe int** c;");
    }

    [Fact]
    public void A_function_returns_a_pointer_to_an_array()
    {
        var emitted = Emit("""
            static int grid[2][3];
            static int (*row(int i))[3] { return &grid[i]; }
            int main(void) { return (*row(1))[2]; }
            """);
        emitted.ShouldContain("internal static unsafe int* row(int i)");
    }

    [Fact]
    public void A_function_returns_a_function_pointer_and_takes_one()
    {
        var emitted = Emit("""
            static int add(int a, int b) { return a + b; }
            static int (*pick(int which, int (*fallback)(int, int)))(int, int) { return which ? add : fallback; }
            int main(void) { return pick(1, add)(2, 3); }
            """);
        emitted.ShouldContain("internal static unsafe delegate*<int, int, int> pick(int which, delegate*<int, int, int> fallback)");
    }

    [Fact]
    public void Nested_declarators_compose_at_file_scope()
    {
        // An array of pointers to arrays, and a pointer to a function returning a
        // pointer to an array: neither had a rule of its own.
        var emitted = Emit("""
            static int grid[2][3];
            static int (*row(int i))[3] { return &grid[i]; }
            static int (*rows[2])[3] = { &grid[1], &grid[0] };
            static int (*(*rowfn)(int))[3] = row;
            int main(void) { return (*rows[0])[1] + (*rowfn(0))[2]; }
            """);
        emitted.ShouldContain("public static unsafe int** rows = (int**)Libc.GlobalArrayZeroed<nint>(2);");
        emitted.ShouldContain("public static unsafe delegate*<int, int*> rowfn = &row;");
    }

    [Fact]
    public void A_pointer_to_an_array_sits_in_a_list()
    {
        // The pointer-to-array declarator was a declaration of its own; it is an
        // init-declarator like any other, at every scope.
        var emitted = Emit("""
            static int grid[2][3];
            int main(void)
            {
                int (*p)[3] = &grid[1], n = 2, *q = grid[0];
                return (*p)[n] + *q;
            }
            """);
        emitted.ShouldContain("int* p = grid + 1 * 3;");
        emitted.ShouldContain("int n = 2;");
        emitted.ShouldContain("int* q = grid + 0 * 3;");
    }

    [Fact]
    public void Parameters_take_every_declarator()
    {
        // A pointer to an array, a multi-dimensional array (adjusted to a pointer to
        // its rows) and a parenthesized name.
        var emitted = Emit("""
            static int sum(int (*m)[3], int g[2][3], int (n)) { return m[0][0] + g[1][2] + n; }
            static int grid[2][3];
            int main(void) { return sum(grid, grid, 2); }
            """);
        emitted.ShouldContain("internal static unsafe int sum(int* m, int* g, int n)");
    }

    [Fact]
    public void Abstract_declarators_name_any_type()
    {
        var emitted = Emit("""
            static int grid[2][3];
            static int sub(int a, int b) { return a - b; }
            int main(void)
            {
                void *raw = grid;
                int (*cast)[3] = (int (*)[3])raw;
                int (*g)(int, int) = (int (*)(int, int))sub;
                return cast[1][2] + g(5, 3) + (int)sizeof(int[4]) + (int)sizeof(char *[3]);
            }
            """);
        emitted.ShouldContain("int* cast = (int*)raw;");
        emitted.ShouldContain("delegate*<int, int, int> g = (delegate*<int, int, int>)&sub;");
        emitted.ShouldContain("(4 * sizeof(int))");
        emitted.ShouldContain("(3 * sizeof(byte*))");
    }

    [Fact]
    public void Parenthesized_declarators_are_the_declarator()
    {
        var emitted = Emit("""
            typedef int T;
            int main(void) { T (x) = 3; int (y) = 4, *(z) = &y, (*(*q)) = &z; return x + **q; }
            """);
        emitted.ShouldContain("int x = 3;");
        emitted.ShouldContain("int** q = &z;");
    }

    [Theory]
    // A body after a declarator that declares no function.
    [InlineData("int x { return 0; }\nint main(void) { return 0; }", "expected '=', ',' or ';' before '{' token")]
    // Only the outermost bound of an array may be omitted (C11 6.7.6.2p1).
    [InlineData("int a[2][];\nint main(void) { return 0; }", "array type has incomplete element type in 'a'")]
    public void Illegal_declarators_are_errors(string source, string message)
        => Rejected(source).ShouldContain(message);

    [Theory]
    // Variably modified types beyond a one-dimensional local array are a loud cut.
    [InlineData("int main(void) { int n = 3; int (*p)[n]; return 0; }", "an array needs constant bounds")]
    [InlineData("int main(void) { int n = 3; return (int)sizeof(int[n]); }", "a variably modified type name")]
    [InlineData("int main(void) { int *p = (int[3]){}; return 0; }", "an empty array compound literal")]
    public void Unsupported_declarators_fail_loudly(string source, string message)
        => Rejected(source).ShouldContain(message);
}

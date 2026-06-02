#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A bare function name used as a value decays to a pointer-to-function
/// (C §6.3.2.1). C# requires the explicit <c>&amp;</c>, so dotcc prepends it at
/// every value position — not just call arguments / scalar fn-ptr decl-inits
/// (already covered), but also aggregate initializers (struct-array elements,
/// C99 designated <c>.field =</c>, compound literals) and plain assignments to
/// a fn-ptr lvalue. This is exactly Lua's ubiquitous <c>luaL_Reg</c> tables
/// (<c>{"name", cfunc}</c>). End-to-end in the <c>fnptr-aggregate-init/</c>
/// fixture; the paren-see-through and <c>@</c>-escape-preserving behaviour of
/// <c>DecayFnName</c> are asserted here.
/// </summary>
public sealed class FnPtrAggregateInitTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fpa-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void struct_array_element_decays_bare_fn_name()
    {
        var src = WriteTemp("""
            typedef int (*fn)(int);
            typedef struct { const char *name; fn func; } reg;
            static int add(int x) { return x + 1; }
            static int mul(int x) { return x * 2; }
            static const reg tbl[] = { {"add", add}, {"mul", mul} };
            int main(void) { return tbl[0].func(0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Each element's `func` is a bare fn name → must take the `&`.
            emitted.ShouldContain("func = &add");
            emitted.ShouldContain("func = &mul");
            // The string field is untouched (not a fn name).
            emitted.ShouldContain("name = Libc.L(\"add\\0\"u8)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void designated_initializer_decays_bare_fn_name()
    {
        var src = WriteTemp("""
            typedef int (*fn)(int);
            typedef struct { const char *name; fn func; } reg;
            static int neg(int x) { return -x; }
            int main(void) { reg r = { .name = "neg", .func = neg }; return r.name[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("func = &neg");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void assignment_to_fnptr_lvalue_decays_bare_fn_name()
    {
        var src = WriteTemp("""
            typedef int (*fn)(int);
            static int add1(int x) { return x + 1; }
            int main(void) { fn g; g = add1; return g(0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("g = &add1");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void parenthesized_fn_name_argument_decays_through_parens()
    {
        // A parenthesized bare fn name as a call argument (`reg((cb))`) still
        // decays — DecayFnName sees through the outer parens, emitting `&cb`.
        var src = WriteTemp("""
            typedef int (*fn)(int);
            static int cb(int x) { return x; }
            static int reg(fn f) { return f(7); }
            int main(void) { return reg((cb)); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("&cb");
            emitted.ShouldNotContain("((cb))");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_fn_name_value_is_left_alone()
    {
        // A struct field initialized with a non-fn value must NOT get a `&`.
        var src = WriteTemp("""
            typedef struct { int a; int b; } pt;
            static const pt p[] = { {1, 2}, {3, 4} };
            int main(void) { return p[0].a; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // No `&` is injected — a decayed value would read `a = &1` / `b = &2`.
            emitted.ShouldContain("a = 1");
            emitted.ShouldContain("b = 2");
        }
        finally { File.Delete(src); }
    }
}

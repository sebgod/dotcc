#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the parenthesized function-name declarator <c>T (name)(args)</c>
/// — Lua's macro-guard idiom in its public headers (<c>LUA_API lua_State
/// *(lua_newstate)(…)</c>). The parens are stripped; the emitted method is
/// identical to the bare form. End-to-end in the <c>paren-fn-name/</c> fixture.
/// </summary>
[Collection("ParenFnName")]
public sealed class ParenFnNameTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-paren-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void parenthesized_definition_emits_bare_method()
    {
        var src = WriteTemp("""
            int (add)(int a, int b) { return a + b; }
            int main(void) { return add(2, 3); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int add(int a, int b)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void parenthesized_prototype_then_definition_parses()
    {
        var src = WriteTemp("""
            int (twice)(int x);
            int (twice)(int x) { return x * 2; }
            int main(void) { return twice(21); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int twice(int x)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void parenthesized_extern_prototype_with_pointer_return_parses()
    {
        // The exact lua.h shape: `extern T *(name)(args);` (LUA_API → extern).
        var src = WriteTemp("""
            extern char *(dup)(char *s);
            int main(void) { return 0; }
            """);
        try
        {
            // Prototype emits nothing, but it must parse without error.
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int main");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void parenthesized_void_param_form_parses()
    {
        var src = WriteTemp("""
            int (answer)(void) { return 42; }
            int main(void) { return answer(); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int answer()");
        }
        finally { File.Delete(src); }
    }
}

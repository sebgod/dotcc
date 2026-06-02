#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A C# <c>using X = Y;</c> alias resolves its RHS IGNORING all other
/// using-aliases, so a scalar typedef-name used in an alias body (an
/// alias-of-alias, or a function-pointer typedef's <c>delegate*&lt;…&gt;</c>)
/// would be an unresolved-type error. dotcc resolves scalar typedef-names there
/// to their underlying C# primitive (<c>ResolveTypedefInType</c>). Also: a
/// <c>(void)</c> fn-ptr parameter list means NO parameters. Lua's lua.h
/// (<c>lua_KContext</c>/<c>lua_Reader</c>) is the motivating case. End-to-end in
/// <c>typedef-alias-resolve/</c>.
/// </summary>
public sealed class TypedefAliasResolveTests
{
    private static string Emit(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tar-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void alias_of_scalar_typedef_resolves_to_primitive()
    {
        // `typedef intptr_t my_ctx;` — the RHS `intptr_t` is itself an alias, so it
        // must resolve to the primitive (a `using my_ctx = intptr_t;` is CS0246).
        var emitted = Emit("""
            #include <stdint.h>
            typedef intptr_t my_ctx;
            int main(void) { my_ctx c = 7; return (int)c; }
            """);
        emitted.ShouldContain("using unsafe my_ctx = long;");
        emitted.ShouldNotContain("using unsafe my_ctx = intptr_t;");
    }

    [Fact]
    public void fnptr_typedef_body_resolves_scalar_components()
    {
        // The `delegate*<…>` body references size_t — must become `ulong*`, not a
        // bare (unresolvable-in-alias-body) `size_t*`.
        var emitted = Emit("""
            #include <stddef.h>
            typedef int (*reader)(size_t *sz, void *ud);
            int main(void) { reader r = 0; return r == 0; }
            """);
        emitted.ShouldContain("using unsafe reader = delegate*<ulong*, void*, int>;");
        // the alias body must not carry the bare (alias-unresolvable) size_t name
        emitted.ShouldNotContain("delegate*<size_t");
    }

    [Fact]
    public void void_param_in_fnptr_typedef_means_no_params()
    {
        // `typedef void (*noargs)(void);` -> `delegate*<void>` (no params), NOT
        // `delegate*<void, void>` (CS1536: void can't be a parameter type).
        var emitted = Emit("""
            typedef void (*noargs)(void);
            int main(void) { noargs f = 0; return f == 0; }
            """);
        emitted.ShouldContain("using unsafe noargs = delegate*<void>;");
        emitted.ShouldNotContain("delegate*<void, void>");
    }
}

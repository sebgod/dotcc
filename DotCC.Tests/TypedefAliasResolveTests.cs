#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The typed IR expands scalar typedefs to their underlying C# primitive at
/// every use site (no <c>using unsafe</c> alias emitted). A
/// <c>(void)</c> fn-ptr parameter list means NO parameters — the
/// <c>delegate*&lt;void&gt;</c> form (one return-type slot) must be used, NOT
/// <c>delegate*&lt;void, void&gt;</c> (CS1536). Lua's lua.h
/// (<c>lua_KContext</c>/<c>lua_Reader</c>) is the motivating case. End-to-end in
/// <c>typedef-alias-resolve/</c>.
/// </summary>
[Collection("TypedefAliasResolve")]
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
        // `typedef intptr_t my_ctx;` — the IR expands the typedef at every use
        // site, so `my_ctx c` emits as `long c` (the underlying C# primitive).
        // No `using unsafe my_ctx = …` alias is emitted.
        var emitted = Emit("""
            #include <stdint.h>
            typedef intptr_t my_ctx;
            int main(void) { my_ctx c = 7; return (int)c; }
            """);
        emitted.ShouldContain("long c = 7");
        emitted.ShouldNotContain("using unsafe my_ctx");
    }

    [Fact]
    public void fnptr_typedef_body_resolves_scalar_components()
    {
        // The IR expands `reader` to its underlying `delegate*<ulong*, void*, int>`
        // at every use site; `size_t*` resolves to `ulong*` so no bare alias name
        // leaks into the emitted C#.
        var emitted = Emit("""
            #include <stddef.h>
            typedef int (*reader)(size_t *sz, void *ud);
            int main(void) { reader r = 0; return r == 0; }
            """);
        emitted.ShouldContain("delegate*<ulong*, void*, int> r = null");
        // the alias body must not carry the bare (unexpanded) size_t name
        emitted.ShouldNotContain("delegate*<size_t");
    }

    [Fact]
    public void void_param_in_fnptr_typedef_means_no_params()
    {
        // `typedef void (*noargs)(void);` — the IR expands `noargs` to
        // `delegate*<void>` (no params) at the use site. Must NOT be
        // `delegate*<void, void>` (CS1536: void can't be a parameter type).
        var emitted = Emit("""
            typedef void (*noargs)(void);
            int main(void) { noargs f = 0; return f == 0; }
            """);
        emitted.ShouldContain("delegate*<void> f = null");
        emitted.ShouldNotContain("delegate*<void, void>");
    }
}

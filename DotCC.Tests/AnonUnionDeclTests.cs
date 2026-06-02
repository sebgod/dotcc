#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for an ANONYMOUS union type used directly in a declaration
/// (`union { … } v;` / `static const union { … } u = {…}`) — distinct from a
/// typedef body or a union member. Lua lstrlib's native-endianness probe. dotcc
/// synthesizes a name (`__NestU&lt;N&gt;`), emits it as an explicit-layout
/// (overlapping) struct, records its fields, and returns the synth name as the
/// Type so the ordinary declaration / aggregate-init productions compose (a brace
/// init targets the FIRST union member). End-to-end in `anon-union-decl/`.
/// </summary>
public sealed class AnonUnionDeclTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-aud-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void file_scope_static_anon_union_with_brace_init_lowers_to_explicit_synth_type()
    {
        var src = WriteTemp("""
            static const union { int dummy; char bytes[4]; } u = {0x04030201};
            int main(void) { return u.dummy; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // explicit-layout (overlapping = C union) synth type
            emitted.ShouldContain("LayoutKind.Explicit");
            emitted.ShouldContain("unsafe struct __NestU0");
            emitted.ShouldContain("FieldOffset(0)] public int dummy;");
            // brace init targets the first union member
            emitted.ShouldContain("new __NestU0 { dummy = 0x04030201 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void block_scope_anon_union_variable_lowers_to_synth_type()
    {
        var src = WriteTemp("""
            int main(void) { union { int i; unsigned x; } v; v.i = -1; return (int)v.x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("LayoutKind.Explicit");
            emitted.ShouldContain("unsafe struct __NestU0");
            emitted.ShouldContain("__NestU0 v = default;");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void typedef_anon_union_is_unaffected()
    {
        // Regression guard: the new anon-union Type production must NOT capture
        // `typedef union { … } Name;` — that still lowers to a named union-struct
        // `Name` (via typedefUnionAnon), not a synth `__NestU` + alias.
        var src = WriteTemp("""
            typedef union { int a; float b; } U;
            int main(void) { U u; u.a = 7; return u.a; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("unsafe struct U");
            emitted.ShouldNotContain("__NestU");
        }
        finally { File.Delete(src); }
    }
}

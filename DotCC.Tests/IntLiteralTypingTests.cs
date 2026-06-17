#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for integer-constant typing per C99 6.4.4.1 — the type of a literal
/// is the first in its candidate list (keyed on base + suffix) that can hold the
/// value. dotcc's model collapses the standard's lists to int / unsigned (32-bit)
/// and long / unsigned long (64-bit, also covering long long). Observed here via
/// the C# backend's literal suffix (<c>RenderIntLit</c> = digits + type suffix):
/// none = int, <c>u</c> = unsigned, <c>L</c> = long, <c>UL</c> = unsigned long.
/// The C# backend previously masked mis-typed literals (C# re-types the text); the
/// wat backend exposed it, so this pins the front-end rule directly.
/// </summary>
[Collection("IntLiteralTyping")]
public sealed class IntLiteralTypingTests
{
    /// <summary>Emit a program whose <c>main</c> declares a local initialised by the
    /// literal, so the literal surfaces (with its type suffix) in the C# output.</summary>
    private static string EmitInit(string cType, string literal)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-lit-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, $"int main(void){{ {cType} x = {literal}; return 0; }}");
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void a_small_decimal_literal_stays_int()
    {
        EmitInit("int", "42").ShouldContain("= 42;");
    }

    [Fact]
    public void a_decimal_over_int_max_becomes_long()
    {
        // Decimal, unsuffixed: int → long. 10000000000 > INT_MAX → long ("L").
        EmitInit("long", "10000000000").ShouldContain("10000000000L");
    }

    [Fact]
    public void a_decimal_between_int_and_uint_max_is_long_not_uint()
    {
        // An unsuffixed DECIMAL constant never picks unsigned: 4000000000 (> INT_MAX,
        // < UINT_MAX) is long, not unsigned int.
        var cs = EmitInit("long", "4000000000");
        cs.ShouldContain("4000000000L");
        cs.ShouldNotContain("4000000000u");
    }

    [Fact]
    public void a_hex_constant_may_pick_unsigned_without_a_suffix()
    {
        // Hex/octal candidate lists include the unsigned types: 0xFFFFFFFF (> INT_MAX,
        // ≤ UINT_MAX) is unsigned int ("u"), where the same decimal value would be long.
        EmitInit("unsigned", "0xFFFFFFFF").ShouldContain("0xFFFFFFFFu");
    }

    [Fact]
    public void the_u_suffix_forces_unsigned()
    {
        EmitInit("unsigned", "7u").ShouldContain("7u");
    }

    [Fact]
    public void the_ll_suffix_forces_long()
    {
        // long long collapses to dotcc's 64-bit long ("L").
        EmitInit("long", "5ll").ShouldContain("5L");
    }
}

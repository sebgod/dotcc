#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Parse-and-lower tests for the <c>const</c> qualifier. <c>const</c> (and
/// <c>volatile</c>) are no longer token-stripped — they parse as Type PREFIX +
/// POSTFIX productions and flag the lowered <see cref="CType"/> (the flag drives
/// the const-correctness check and the read-only-array RVA fast path). These
/// assert the interesting positions PARSE (a qualifier before/after a typedef-name
/// or struct tag, where the grammar once had no production) and that the emitter
/// still drops the <c>const</c> keyword from the C# output — a C# local can't carry
/// C's <c>const</c> (it allows runtime init), so the qualifier rides the CType, not
/// the emitted text. The run-it-end-to-end check lives in the <c>const-qualifiers/</c>
/// functional fixture; const-violation diagnosing lives in <c>ConstCheckTests</c>.
/// </summary>
[Collection("ConstQualifier")]
public sealed class ConstQualifierTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-const-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void west_const_before_typedef_name_parses()
    {
        // `const <TYPE_NAME>` — a qualifier prefixing a typedef-name. The typedef
        // expands to its primitive; the emitter drops the const keyword.
        var src = WriteTemp("""
            typedef int MyInt;
            int main(void) { const MyInt x = 5; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = 5");
            emitted.ShouldNotContain("const int x");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void east_const_on_a_builtin_parses()
    {
        // `int const` — east-const on a builtin base, via the Type POSTFIX
        // production. Same lowering as `const int`.
        var src = WriteTemp("""
            int main(void) { int const x = 7; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int x = 7");
            emitted.ShouldNotContain("const int x");
            emitted.ShouldNotContain("int const x");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_volatile_and_east_const_on_typedef_pointer_parse()
    {
        // West `const volatile T*`, east `T const *`, and a const cast — all
        // through a typedef-name.
        var src = WriteTemp("""
            typedef struct Pt { int x; int y; } Pt;
            static int sx(const volatile Pt *a, Pt const *b) { return a->x + b->y; }
            int main(void) {
                Pt p = { 1, 2 };
                return sx((const Pt *)&p, &p);
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int sx(");
            // No qualifier keyword survives onto the user's signature/cast (the
            // spliced runtime block legitimately uses `const` for its own
            // constants, so assert against the qualified spellings, not bare "const").
            emitted.ShouldNotContain("const volatile");
            emitted.ShouldNotContain("const Pt");
            emitted.ShouldNotContain("Pt const");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_before_struct_tag_parses()
    {
        // `const struct Box *` — qualifier in front of a struct tag.
        var src = WriteTemp("""
            struct Box { int w; int h; };
            static int area(const struct Box *b) { return b->w * b->h; }
            int main(void) { struct Box b = { 3, 4 }; return area(&b); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("static unsafe int area(");
            emitted.ShouldNotContain("const struct");
        }
        finally { File.Delete(src); }
    }
}

#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for <see cref="QualifierStripper"/> — the token-level removal of
/// <c>const</c>/<c>volatile</c>. The behaviourally interesting cases are the
/// ones the grammar could not parse before the stripper (a qualifier in front
/// of a typedef-name or a struct tag); we also assert that no qualifier leaks
/// into the emitted C# (dotcc has no const/volatile representation). The full
/// run-it-end-to-end check lives in the <c>const-qualifiers/</c> functional
/// fixture.
/// </summary>
public sealed class QualifierStripperTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-qual-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void const_before_typedef_name_parses()
    {
        // `const <TYPE_NAME>` had no production — a qualifier can't prefix a
        // typedef-name in the grammar, and adding one is LALR-ambiguous. The
        // stripper removes the token so this reduces as a plain `MyInt` decl.
        var src = WriteTemp("""
            typedef int MyInt;
            int main(void) { const MyInt x = 5; return x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("MyInt");
            // The qualifier is gone — no `const MyInt` (C# `const` has different
            // semantics and a `const` field/local is not what we emit).
            emitted.ShouldNotContain("const MyInt");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_volatile_and_east_const_on_typedef_pointer_parse()
    {
        // West `const volatile T*`, east `T const *`, and a const cast — all
        // through a typedef-name. Pre-stripper these were parse errors.
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
            // No qualifier survives onto the user's signature/cast (the spliced
            // runtime block legitimately uses `const` for its own constants, so
            // assert against the specific qualified spellings, not bare "const").
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

    [Fact]
    public void volatile_local_is_dropped()
    {
        var src = WriteTemp("""
            int main(void) { volatile int n = 3; return n; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int n = 3");
            emitted.ShouldNotContain("volatile int");
        }
        finally { File.Delete(src); }
    }
}

#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for named nested aggregate members — <c>struct { … } name;</c> and
/// <c>union { … } name;</c>. Unlike the anonymous (C11) forms, the field is NOT
/// promoted: it's a real field of a synthesized nested type, accessed as
/// <c>o.name.inner</c>. End-to-end behavior is in the <c>named-nested-member/</c>
/// fixture.
/// </summary>
public sealed class NamedNestedMemberTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-nnm-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void named_nested_struct_emits_synth_type_field()
    {
        var src = WriteTemp("""
            struct Outer { int tag; struct { int x; int y; } pt; };
            int main(void) { struct Outer o; o.pt.x = 3; return o.pt.x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // A synthesized sequential nested struct, plus a field of it named `pt`.
            emitted.ShouldContain("struct __NestS");
            emitted.ShouldContain(" pt;");
            // NOT promoted: the inner fields don't become parent fields.
            emitted.ShouldContain("o.pt.x");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void named_nested_union_member_gets_explicit_layout()
    {
        var src = WriteTemp("""
            struct Outer { union { int i; float f; } u; };
            int main(void) { struct Outer o; o.u.f = 1.5f; return (int)o.u.f; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("struct __NestU");
            emitted.ShouldContain("LayoutKind.Explicit");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void named_nested_struct_inside_union_typedef_parses()
    {
        // The Lua StackValue shape: a named nested struct member inside a union.
        var src = WriteTemp("""
            typedef union StackValue {
                long val;
                struct { int lo; unsigned short delta; } tbc;
            } StackValue;
            int main(void) { StackValue s; s.tbc.delta = 9; return (int)s.tbc.delta; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("s.tbc.delta");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void anonymous_struct_member_still_promotes()
    {
        // Regression guard: the anonymous form must STILL inline/promote (s.x),
        // after refactoring it onto the shared MemberMark.
        var src = WriteTemp("""
            struct Outer { int tag; struct { int x; int y; }; };
            int main(void) { struct Outer o; o.x = 5; return o.x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("o.x");           // promoted — direct access
            emitted.ShouldNotContain("__NestS");    // no synth type for the anon form
        }
        finally { File.Delete(src); }
    }
}

#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// GH #246: a C99 flexible array member (<c>T d[]</c>) and a GNU zero-length one
/// (<c>T d[0]</c>) have no storage of their own. The layout model places each after the
/// members before it (aligned to its element) and sizes the struct as C does, the C#
/// struct takes that size, and an access is the member's address in the object. A
/// static object's initializer may give a flexible member elements (a GNU extension):
/// the object then lives in a native block with room for them. gcc's constraints and
/// -pedantic diagnostics apply. End-to-end in <c>flexible-array-storage/</c>
/// (gcc-matched).
/// </summary>
[Collection("FlexibleArrayMember")]
public sealed class FlexibleArrayMemberTests
{
    private static string Emit(string body, WarningFlags warnings = WarningFlags.Default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-fam-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        try { return Compiler.EmitCSharp(new[] { path }, warnings: warnings); }
        finally { File.Delete(path); }
    }

    private static string Rejected(string body, WarningFlags warnings = WarningFlags.Default)
        => Should.Throw<CompileException>(() => Emit(body, warnings)).Message;

    [Fact]
    public void A_flexible_member_is_its_offset_in_the_object()
    {
        var emitted = Emit("""
            struct wide { char tag; int vals[]; };
            int get(struct wide *w, int i) { return w->vals[i]; }
            int main(void) { return 0; }
            """);
        // Aligned to its element: C's size is 4, where the kept `tag` alone would be 1.
        emitted.ShouldContain("LayoutKind.Sequential, Size = 4)]\nunsafe struct wide\n{\n    public byte tag;\n}");
        emitted.ShouldContain("return ((int*)((byte*)w + 4))[i];");
    }

    [Fact]
    public void A_zero_length_member_of_anonymous_structs_is_indexed_through_its_offset()
    {
        var emitted = Emit("""
            struct tmpl { int count; struct { int index; void *literal; } items[0]; };
            int second(struct tmpl *t) { return t->items[1].index; }
            int main(void) { return (int)sizeof(struct tmpl); }
            """);
        emitted.ShouldContain("LayoutKind.Sequential, Size = 8)]\nunsafe struct tmpl");
        emitted.ShouldContain("return ((__Anon0*)((byte*)t + 8))[1].index;");
    }

    [Fact]
    public void A_static_initializer_gives_the_flexible_member_storage()
    {
        var emitted = Emit("""
            struct keys { long size; char kind; signed char indices[]; };
            static struct keys empty = { 2, 'e', -1, -1 };
            int main(void) { return empty.indices[1] + (&empty)->indices[0] + 2; }
            """);
        emitted.ShouldContain("var p = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(16);");
        emitted.ShouldContain("*(keys*)p = new keys { size = 2, kind = 101 };");
        emitted.ShouldContain("var tail = (sbyte*)(p + 9);");
        emitted.ShouldContain("tail[1] = (sbyte)(-1);");
        emitted.ShouldContain("public static unsafe ref keys empty => ref *(keys*)__fam_empty;");
    }

    [Theory]
    [InlineData("struct S { int n; char d[]; };\nint main(void) { struct S s = { 1, { 2 } }; return s.n; }",
        "non-static initialization of a flexible array member")]
    [InlineData("struct S { int n; char d[]; };\nstruct O { int a; struct S s; };\nstatic struct O o = { 1, { 2, { 3 } } };\nint main(void) { return o.a; }",
        "initialization of flexible array member in a nested context")]
    [InlineData("struct S { int n; char d[]; };\nunsigned long f(struct S *p) { return sizeof(p->d); }\nint main(void) { return 0; }",
        "invalid application of 'sizeof' to incomplete type 'char[]'")]
    public void Flexible_member_constraints_are_gcc_errors(string source, string message)
        => Rejected(source).ShouldContain(message);

    [Theory]
    [InlineData("struct S { int n; char d[0]; };\nint main(void) { return 0; }", "ISO C forbids zero-size array 'd'")]
    [InlineData("struct S { int n; char d[]; };\nstatic struct S s = { 1, { 2 } };\nint main(void) { return s.n; }", "initialization of a flexible array member")]
    public void Pedantic_diagnoses_the_gnu_extensions(string source, string message)
    {
        Emit(source).ShouldContain("int main()");
        Rejected(source, WarningFlags.Default | WarningFlags.PedanticErrors).ShouldContain(message);
    }
}

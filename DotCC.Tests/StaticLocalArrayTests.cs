#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for block-scope <c>static</c> array declarations. A C local
/// static array has the same static storage duration as a file-scope array, so
/// dotcc lowers it to a pinned global field (<c>Libc.GlobalArray*</c>) under a
/// sequentially-suffixed name (<c>{name}__s{N}</c>), with in-function uses
/// rewritten to that field — exactly the scalar static-local path. An array OF
/// POINTERS is stored as a pinned <c>nint[]</c> reinterpreted as <c>T**</c>
/// because C# forbids pointer types as generic type arguments / array elements
/// (CS0306 / CS0611) — this also fixes the same latent bug on the file-scope
/// path. End-to-end in the <c>static-local-array/</c> fixture.
/// </summary>
public sealed class StaticLocalArrayTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sla-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void scalar_static_local_array_hoists_to_mangled_global_field()
    {
        var src = WriteTemp("""
            int f(unsigned int x) {
                static const unsigned char tab[4] = {0, 1, 2, 3};
                return tab[x];
            }
            int main(void) { return f(2); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Hoisted to a mangled global field; the array is `const`, so it takes
            // the zero-copy RVA path (Libc.L over .rodata) rather than GlobalArrayFrom.
            emitted.ShouldContain("tab__s0 = Libc.L(new byte[]{ 0, 1, 2, 3 })");
            // …and the in-function use rewrites to that field.
            emitted.ShouldContain("tab__s0[x]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void implicit_size_static_local_array_works()
    {
        var src = WriteTemp("""
            int f(int i) {
                static const int tab[] = {10, 20, 30};
                return tab[i];
            }
            int main(void) { return f(0); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("tab__s0 = Libc.GlobalArrayFrom<int>(new int[]{ 10, 20, 30 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void pointer_element_static_local_array_uses_nint_reinterpret()
    {
        // C# can't make `byte*[]` or `GlobalArrayFrom<byte*>` — store the
        // pointers as a pinned nint[] and reinterpret the base as `byte**`.
        var src = WriteTemp("""
            const char *name(int i) {
                static const char *const names[] = {"a", "b"};
                return names[i];
            }
            int main(void) { return name(0)[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("names__s0 = (byte**)Libc.GlobalArrayFrom<nint>(new nint[]{ (nint)(Libc.L(\"a\\0\"u8)), (nint)(Libc.L(\"b\\0\"u8)) })");
            emitted.ShouldNotContain("GlobalArrayFrom<byte*>");
            emitted.ShouldNotContain("new byte*[]");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void same_named_static_locals_in_two_functions_dont_collide()
    {
        var src = WriteTemp("""
            int f(int i) { static const int t[2] = {1, 2}; return t[i]; }
            int g(int i) { static const int t[2] = {3, 4}; return t[i]; }
            int main(void) { return f(0) + g(1); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // Mangled per declaration order → two distinct fields.
            emitted.ShouldContain("t__s0 = Libc.GlobalArrayFrom<int>(new int[]{ 1, 2 })");
            emitted.ShouldContain("t__s1 = Libc.GlobalArrayFrom<int>(new int[]{ 3, 4 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void file_scope_pointer_array_also_uses_nint_reinterpret()
    {
        // Regression: a file-scope array OF POINTERS hit the same CS0306/CS0611
        // (it emitted `GlobalArrayFrom<byte*>(new byte*[]{…})`, invalid C# that
        // the parse-only probe never caught). The pointer lowering fixes both.
        var src = WriteTemp("""
            const char *const names[] = {"x", "y", "z"};
            int main(void) { return names[0][0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte** names = (byte**)Libc.GlobalArrayFrom<nint>(new nint[]{");
            emitted.ShouldNotContain("GlobalArrayFrom<byte*>");
            emitted.ShouldNotContain("new byte*[]");
        }
        finally { File.Delete(src); }
    }
}

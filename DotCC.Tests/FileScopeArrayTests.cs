#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for file-scope (global) array declarations: each lowers to a
/// <c>T*</c> field in <c>DotCcGlobals</c> backed by a pinned, rooted managed
/// array via the <c>Libc.GlobalArray*</c> helpers. End-to-end behavior (incl.
/// the <c>sizeof(a)/sizeof(a[0])</c> idiom and multi-dim subscripting) is in the
/// <c>file-scope-arrays/</c> functional fixture.
/// </summary>
[Collection("FileScopeArray")]
public sealed class FileScopeArrayTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-ga-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void zeroed_global_array_uses_GlobalArrayZeroed()
    {
        var src = WriteTemp("""
            int scratch[8];
            int main(void) { scratch[0] = 1; return scratch[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* scratch = Libc.GlobalArrayZeroed<int>(8)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void initialized_global_array_uses_GlobalArrayFrom()
    {
        var src = WriteTemp("""
            int t[4] = { 10, 20, 30, 40 };
            int main(void) { return t[2]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* t = Libc.GlobalArrayFrom<int>(new int[]{ 10, 20, 30, 40 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sized_init_zero_fills_tail()
    {
        // `int s[6] = {0,1,4,9}` — C zero-fills the unspecified tail.
        var src = WriteTemp("""
            static int s[6] = { 0, 1, 4, 9 };
            int main(void) { return s[5]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("new int[]{ 0, 1, 4, 9, 0, 0 }");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_char_array_from_string_decodes_with_nul_and_uses_rva()
    {
        var src = WriteTemp("""
            static const char tag[] = "hi";
            int main(void) { return tag[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // 'h'=104, 'i'=105, NUL=0. A const byte array is read-only, so it lowers
            // to the zero-copy RVA path (Libc.L over PE .rodata) rather than the
            // writable GlobalArrayFrom POH copy.
            emitted.ShouldContain("byte* tag = Libc.L(new byte[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_const_char_array_stays_writable_GlobalArrayFrom()
    {
        // Without const the array may be mutated (here it is), so it keeps the
        // writable GlobalArrayFrom POH path — no RVA.
        var src = WriteTemp("""
            static char buf[] = "hi";
            int main(void) { buf[0] = 'H'; return buf[0]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("byte* buf = Libc.GlobalArrayFrom<byte>(new byte[]{ 104, 105, 0 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void const_multibyte_array_uses_generic_rva()
    {
        // A const non-byte primitive array RVAs through the generic Libc.L<T>
        // (writing to it is UB / rejected by the check), all-constant elements
        // so Roslyn folds the backing array into .rodata.
        var src = WriteTemp("""
            static const int tab[] = { 10, 20, 30 };
            int main(void) { return tab[1]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* tab = Libc.L<int>(new int[]{ 10, 20, 30 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_const_int_array_stays_writable_GlobalArrayFrom()
    {
        var src = WriteTemp("""
            static int tab[] = { 10, 20, 30 };
            int main(void) { tab[0] = 99; return tab[1]; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("int* tab = Libc.GlobalArrayFrom<int>(new int[]{ 10, 20, 30 })");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void extern_array_declaration_emits_no_field()
    {
        var src = WriteTemp("""
            extern const int shared[];
            int main(void) { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // No storage for an extern declaration (the definition is elsewhere).
            emitted.ShouldNotContain("shared = Libc.GlobalArray");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void global_array_sizeof_idiom_resolves_element_count()
    {
        // sizeof(a)/sizeof(a[0]) on a global must yield the element count, not 8/4.
        var src = WriteTemp("""
            int a[5] = { 1, 2, 3, 4, 5 };
            int main(void) { return (int)(sizeof(a) / sizeof(a[0])); }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // count * sizeof(element) = 5 * sizeof(int)
            emitted.ShouldContain("5 * sizeof(int)");
        }
        finally { File.Delete(src); }
    }
}

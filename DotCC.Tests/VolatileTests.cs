#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for faithful <c>volatile</c> lowering (phase V1). A volatile lvalue
/// of eligible scalar type reads through <c>Volatile.Read(ref …)</c> and writes
/// through <c>Volatile.Write(ref …, …)</c> — C's "do not elide/reorder this
/// access" guarantee, rather than the old erase-the-qualifier behaviour. A
/// non-eligible volatile lvalue (struct / pointer / enum) falls back to a plain
/// access for now. End-to-end behaviour is checked by the <c>volatile-access/</c>
/// functional fixture.
/// </summary>
public sealed class VolatileTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-vol-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void volatile_local_read_and_write_use_Volatile_api()
    {
        var src = WriteTemp("""
            int main(void) { volatile int n = 0; n = 5; return n; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The declaration itself is a plain field (`int n = 0`) — volatile is a
            // store/load property, not a storage difference.
            emitted.ShouldContain("int n = 0");
            // Write → Volatile.Write; read → Volatile.Read.
            emitted.ShouldContain("Volatile.Write(ref n, 5)");
            emitted.ShouldContain("Volatile.Read(ref n)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void volatile_compound_assign_is_fenced_read_modify_write()
    {
        var src = WriteTemp("""
            int main(void) { volatile int n = 1; n += 3; return n; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // `n += 3` → Volatile.Write(ref n, Volatile.Read(ref n) + 3).
            emitted.ShouldContain("Volatile.Write(ref n, global::System.Threading.Volatile.Read(ref n) + 3)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void volatile_struct_field_uses_Volatile_api()
    {
        // Lua's `volatile sig_atomic_t trap;` shape — a volatile scalar member.
        var src = WriteTemp("""
            struct S { volatile int trap; int n; };
            int main(void) { struct S s; s.trap = 1; s.n = 2; return s.trap + s.n; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Volatile.Write(ref s.trap, 1)");
            emitted.ShouldContain("Volatile.Read(ref s.trap)");
            // The non-volatile sibling field stays a plain access.
            emitted.ShouldContain("(s.n) = 2");
            emitted.ShouldNotContain("Volatile.Write(ref s.n");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void volatile_field_through_pointer_uses_Volatile_api()
    {
        var src = WriteTemp("""
            struct S { volatile int trap; };
            static void set(struct S *p) { p->trap = 7; }
            int main(void) { struct S s; set(&s); return s.trap; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Volatile.Write(ref p->trap, 7)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void address_of_volatile_is_the_bare_lvalue()
    {
        // `&n` takes the object's address — NOT a read — so it must be `&n`,
        // not `&Volatile.Read(ref n)` (which wouldn't compile).
        var src = WriteTemp("""
            int main(void) { volatile int n = 4; int *p = &n; return *p; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("(&n)");
            emitted.ShouldNotContain("&global::System.Threading.Volatile.Read");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void non_eligible_volatile_struct_local_falls_back_to_plain()
    {
        // A volatile lvalue of non-eligible type (a struct value) has no
        // Volatile.Read/Write overload, so it falls back to a plain access (V1
        // covers eligible scalars; documented).
        var src = WriteTemp("""
            typedef struct Pt { int x; int y; } Pt;
            int main(void) { volatile Pt p = { 1, 2 }; return p.x; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("Pt p = new Pt");
            emitted.ShouldNotContain("Volatile.Read(ref p)");
        }
        finally { File.Delete(src); }
    }
}

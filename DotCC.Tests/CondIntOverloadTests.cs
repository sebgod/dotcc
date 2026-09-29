#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// A C scalar of any integer type is truthy when non-zero. dotcc tests a
/// controlling expression of a known scalar type in place (<c>x != 0</c>); what
/// remains goes through <c>Cond.B(...)</c>, whose overloaded static class must
/// carry an exact overload for every lowered numeric type. Without them a
/// <c>byte</c> / <c>uint</c> / <c>long</c> / <c>ulong</c> / … argument is
/// convertible to BOTH a built-in numeric overload AND <c>CBool</c> (one
/// user-defined step), so the call is ambiguous (CS0121) — Lua's
/// <c>if (ls-&gt;allowhook)</c> (a <c>lu_byte</c>). End-to-end in the
/// <c>cond-int-types/</c> fixture; here we assert the emitted shell carries the
/// overloads.
/// </summary>
[Collection("CondIntOverload")]
public sealed class CondIntOverloadTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-cio-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void cond_class_has_overloads_for_every_numeric_type()
    {
        var src = WriteTemp("int main(void) { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            // The previously-present forms…
            emitted.ShouldContain("public static bool B(int x)");
            emitted.ShouldContain("public static bool B(double x)");
            // …plus the unsigned / wide / narrow integer + float forms.
            emitted.ShouldContain("public static bool B(uint x)");
            emitted.ShouldContain("public static bool B(long x)");
            emitted.ShouldContain("public static bool B(ulong x)");
            emitted.ShouldContain("public static bool B(nint x)");
            emitted.ShouldContain("public static bool B(nuint x)");
            emitted.ShouldContain("public static bool B(byte x)");
            emitted.ShouldContain("public static bool B(sbyte x)");
            emitted.ShouldContain("public static bool B(short x)");
            emitted.ShouldContain("public static bool B(ushort x)");
            emitted.ShouldContain("public static bool B(float x)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void unsigned_and_wide_int_conditions_compile_shape()
    {
        // An integer controlling expression is tested against zero in place, at
        // its own width (no Cond.B call, so no overload to resolve).
        var src = WriteTemp("""
            int main(void) {
                unsigned long ul = 5;
                unsigned char b = 1;
                if (ul) return 1;
                if (b)  return 2;
                return 0;
            }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });
            emitted.ShouldContain("if (ul != 0)");
            emitted.ShouldContain("if (b != 0)");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void A_member_of_a_runtime_owned_struct_is_tested_from_its_type()
    {
        // `struct tm` is the runtime's C# type, and <time.h> declares its body for the IR
        // (GH #250), so `p->tm_zone` is typed `const char *` and tests as a pointer
        // (CPython's timemodule.c get_zone). Before, it read as a placeholder int, and a
        // `!= 0` test would not have compiled (CS0019).
        var src = WriteTemp("""
            #include <time.h>
            const char *zone(struct tm *p) { return p->tm_zone ? p->tm_zone : "   "; }
            int main(void) { return 0; }
            """);
        try
        {
            var cs = Compiler.EmitCSharp(new[] { src });
            cs.ShouldContain("(p->tm_zone != null ? p->tm_zone : ");
            cs.ShouldNotContain("Cond.B(p->tm_zone)");
        }
        finally { File.Delete(src); }
    }
}

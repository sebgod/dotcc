#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DotCC.Frontends;
using DotCC.Ir;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The runtime-owned aggregates (<see cref="RuntimeTypeNames.RuntimeOwnedAggregates"/>: <c>struct tm</c>,
/// <c>struct timespec</c>, <c>struct lconv</c>, <c>div_t</c>, <c>ldiv_t</c>, <c>lldiv_t</c>,
/// <c>imaxdiv_t</c>) are C# types nested in <c>Libc</c>, and the synthetic headers declare the same bodies
/// so the IR knows their fields and layout. The sync test is the safety net: a header body that drifts from
/// its C# type makes <c>sizeof</c> / <c>offsetof</c> fold to a wrong constant, so each is checked field by
/// field against the compiled runtime (name, order, offset, size). The emit pins cover what used to go
/// wrong with no layout (issue #250): a zero initializer emitted as a scalar, a designated initializer
/// rejected, <c>offsetof</c> not constant, and <c>_Generic</c> on <c>ldiv_t.quot</c> choosing <c>int</c>.
/// </summary>
[Collection("RuntimeOwnedAggregate")]
public sealed class RuntimeOwnedAggregateTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-roa-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string EmitC(string body)
    {
        var path = WriteTemp(body);
        try { return Compiler.EmitCSharp(new[] { path }); }
        finally { File.Delete(path); }
    }

    private const string AllHeaders = """
        #include <stdlib.h>
        #include <time.h>
        #include <locale.h>
        #include <inttypes.h>
        """;

    [Fact]
    public void Every_header_body_matches_its_runtime_type()
    {
        var path = WriteTemp(AllHeaders + "\nint main(void) { return 0; }\n");
        IrModule ir;
        try { ir = new CFrontend().BuildIr(new FrontendRequest(new[] { path })); }
        finally { File.Delete(path); }

        foreach (var name in RuntimeTypeNames.RuntimeOwnedAggregates.Order(StringComparer.Ordinal))
        {
            var def = ir.Types.SingleOrDefault(t => t.Name == name);
            def.ShouldNotBeNull($"no synthetic header declares the body of runtime-owned '{name}'");
            def.IsRuntimeOwned.ShouldBeTrue(name);
            def.IsUnion.ShouldBeFalse(name);

            var cs = typeof(DotCC.Libc.Libc).GetNestedType(name, BindingFlags.Public)
                ?? throw new InvalidOperationException($"Libc has no nested type '{name}'");
            cs.IsValueType.ShouldBeTrue(name);
            var csFields = cs.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(f => (long)Marshal.OffsetOf(cs, f.Name))
                .ToList();

            def.Fields.Select(f => f.Name).ShouldBe(csFields.Select(f => f.Name), $"{name}: field names and order");
            for (var i = 0; i < csFields.Count; i++)
            {
                var f = csFields[i];
                var irType = def.Fields[i].Type;
                var where = $"{name}.{f.Name}";
                (irType.Unqualified is CType.Pointer).ShouldBe(f.FieldType.IsPointer, where + ": pointer-ness");
                var csSize = f.FieldType.IsPointer ? IntPtr.Size : Marshal.SizeOf(f.FieldType);
                irType.SizeOf.ShouldBe(csSize, where + ": size");
                ir.OffsetOfConst(name, f.Name).ShouldBe((int)Marshal.OffsetOf(cs, f.Name), where + ": offset");
            }
            ir.SizeOfConst(new CType.Named(name)).ShouldBe(Marshal.SizeOf(cs), name + ": sizeof");
        }
    }

    [Fact]
    public void The_backend_emits_no_second_runtime_owned_type()
    {
        var cs = EmitC(AllHeaders + """

            int main(void) {
                struct tm t = {0};
                struct timespec ts = {0};
                div_t d = div(7, 2);
                return t.tm_sec + (int)ts.tv_sec + d.quot + (localeconv() != 0);
            }
            """);
        foreach (var name in RuntimeTypeNames.RuntimeOwnedAggregates)
        {
            // The runtime's own is nested (indented) in Libc; a second one would be top level.
            Regex.IsMatch(cs, $@"(?m)^[a-z][a-z ]*\bstruct {name}\b").ShouldBeFalse(name);
        }
    }

    [Fact]
    public void A_zero_initialized_tm_is_an_aggregate_initializer()
    {
        var cs = EmitC("""
            #include <time.h>
            int main(void) { struct tm t = {0}; return t.tm_sec; }
            """);
        cs.ShouldContain("tm t = new tm { tm_sec = 0 };");
        cs.ShouldNotContain("tm t = 0;");
    }

    [Fact]
    public void A_designated_timespec_initializer_names_its_fields()
    {
        var cs = EmitC("""
            #include <time.h>
            int main(void) { struct timespec ts = { .tv_sec = 1, .tv_nsec = 2 }; return (int)ts.tv_nsec; }
            """);
        cs.ShouldContain("timespec ts = new timespec { tv_sec = 1, tv_nsec = 2 };");
    }

    [Fact]
    public void Offsetof_a_tm_member_is_a_constant_array_bound()
    {
        // LP64: nine ints (36 bytes), then tm_gmtoff aligned to 8.
        var cs = EmitC("""
            #include <stddef.h>
            #include <time.h>
            char pad[offsetof(struct tm, tm_gmtoff)];
            int main(void) { return (int)sizeof pad; }
            """);
        cs.ShouldContain("GlobalArrayZeroed<byte>(40)");
    }

    [Fact]
    public void Generic_on_ldiv_quot_selects_long()
    {
        var cs = EmitC("""
            #include <stdlib.h>
            int main(void) { long big = 5000000000L; return _Generic(ldiv(big, 2).quot, long: 1, int: 2, default: 3); }
            """);
        cs.ShouldContain("return 1;");
    }

    [Fact]
    public void Tm_zone_compares_as_a_pointer()
    {
        // tm_zone is `char *` (C# byte*). With the int placeholder this emitted `p->tm_zone == 0`,
        // which C# rejects on a pointer (CS0019).
        var cs = EmitC("""
            #include <time.h>
            int f(struct tm *p) { return p->tm_zone == 0; }
            int main(void) { struct tm t = {0}; return f(&t); }
            """);
        cs.ShouldContain("p->tm_zone == null");
    }
}

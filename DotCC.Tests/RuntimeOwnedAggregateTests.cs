#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DotCC.Backends;
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
/// field against the compiled runtime (name, order, C# type, offset, size). The emit pins cover what used to go
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

    /// <summary>Emit and capture stderr, where warnings land. The unit assembly is serialized
    /// (AssemblyInfo), so the process-global <c>Console.Error</c> swap is race-free.</summary>
    private static (string Emit, string Stderr) EmitWithStderr(string body)
    {
        var path = WriteTemp(body);
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { return (Compiler.EmitCSharp(new[] { path }), sw.ToString()); }
        finally { Console.SetError(prior); File.Delete(path); }
    }

    /// <summary>C#'s keyword for each primitive a runtime field can have, as
    /// <see cref="CSharpTarget.RenderType"/> spells it.</summary>
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(sbyte)] = "sbyte", [typeof(byte)] = "byte", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
        [typeof(nint)] = "nint", [typeof(nuint)] = "nuint", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(void)] = "void",
    };

    /// <summary>The C# spelling of a runtime field's type: a keyword for a primitive, <c>T*</c> for a pointer.</summary>
    private static string CsSpelling(Type t) =>
        t.IsPointer && t.GetElementType() is { } element ? CsSpelling(element) + "*"
        : Keywords.TryGetValue(t, out var keyword) ? keyword
        : t.Name;

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

        var target = new CSharpTarget();
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
                // Marshal reports the marshalled layout, which is the managed one only for a blittable field.
                (f.FieldType.IsPointer || f.FieldType.IsPrimitive && f.FieldType != typeof(bool) && f.FieldType != typeof(char))
                    .ShouldBeTrue(where + ": a pointer or a blittable primitive");
                // The same C# type, not just the same size: a header `double` against a C# `long`, or `unsigned int`
                // against `int`, would pass a size check while the IR does the wrong arithmetic.
                target.RenderType(irType).ShouldBe(CsSpelling(f.FieldType), where + ": C# type");
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
    public void An_object_file_has_no_type_record_for_a_runtime_owned_aggregate()
    {
        // CPython links from objects: a type record for `tm` would put a second top-level type in the link.
        var path = WriteTemp(AllHeaders + """

            struct point { int x, y; };
            int f(void) {
                struct tm t = {0};
                struct timespec ts = {0};
                struct point p = {1, 2};
                div_t d = div(7, 2);
                return t.tm_sec + (int)ts.tv_sec + p.x + d.quot + (localeconv() != 0);
            }
            """);
        string obj;
        try { obj = Compiler.EmitObject(path); }
        finally { File.Delete(path); }

        obj.ShouldContain("//!!dotcc-obj type:point\n");
        foreach (var name in RuntimeTypeNames.RuntimeOwnedAggregates)
        {
            Regex.IsMatch(obj, $@"(?m)^//!!dotcc-obj (type|opaque):{name}$").ShouldBeFalse(name);
        }
    }

    [Fact]
    public void A_program_that_defines_its_own_struct_tm_emits_it()
    {
        // Only a body in a synthetic header is runtime-owned. Without <time.h> a program may define its own
        // `struct tm`, which is emitted like any other type (a top-level type wins over `using static Libc;`).
        const string source = "struct tm { int x; };\nint main(void) { struct tm t = { 7 }; return t.x; }\n";
        var path = WriteTemp(source);
        IrModule ir;
        try { ir = new CFrontend().BuildIr(new FrontendRequest(new[] { path })); }
        finally { File.Delete(path); }
        ir.Types.Single(t => t.Name == "tm").IsRuntimeOwned.ShouldBeFalse();

        Regex.IsMatch(EmitC(source), @"(?m)^[a-z][a-z ]*\bstruct tm\b").ShouldBeTrue();
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
        // tm_zone is `const char *` (C# byte*). With the int placeholder this emitted `p->tm_zone == 0`,
        // which C# rejects on a pointer (CS0019).
        var cs = EmitC("""
            #include <time.h>
            int f(struct tm *p) { return p->tm_zone == 0; }
            int main(void) { struct tm t = {0}; return f(&t); }
            """);
        cs.ShouldContain("p->tm_zone == null");
    }

    [Fact]
    public void Tm_zone_takes_a_pointer_to_const()
    {
        // glibc, musl and the BSDs declare `const char *tm_zone`, and CPython's time module stores a
        // `static const char *` into it (timemodule.c), which a `char *` member warned about.
        var (cs, stderr) = EmitWithStderr("""
            #include <time.h>
            static const char *utc = "UTC";
            int main(void) { struct tm t = {0}; t.tm_zone = utc; return t.tm_zone[0] == 'U' ? 0 : 1; }
            """);
        stderr.ShouldNotContain("discards 'const' qualifier");
        cs.ShouldContain("t.tm_zone = utc;");
    }
}

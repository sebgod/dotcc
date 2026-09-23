#nullable enable

using System.Collections.Generic;
using DotCC.Backends;
using DotCC.Ir;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Direct pins for the IR's aggregate registries — <see cref="IrBuilder.RegisterStructType"/> and
/// <see cref="IrBuilder.RegisterEnumType"/>, the shared seam a second front-end registers its types
/// through. The emitted C# carries exactly one type per name, so both must be idempotent on an
/// identical re-registration and must THROW on a conflicting one: silently keeping the first
/// definition while uses resolve against the second is a type/codegen mismatch (the enum registry had
/// no such guard until the 2026-09 architecture review found it).
/// </summary>
public sealed class IrAggregateRegistryTests
{
    private static IrBuilder NewBuilder() => new(null, new CSharpNameLegalizer());

    [Fact]
    public void An_identical_enum_re_registration_is_idempotent()
    {
        var ir = NewBuilder();
        var a = ir.RegisterEnumType("Kind", CType.Int, new List<EnumMember> { new("x", 0), new("y", 1) });
        var b = ir.RegisterEnumType("Kind", CType.Int, new List<EnumMember> { new("x", 0), new("y", 1) });
        b.ShouldBe(a);
        ir.Enums.Count.ShouldBe(1);
    }

    [Fact]
    public void A_conflicting_enum_re_registration_throws()
    {
        var ir = NewBuilder();
        ir.RegisterEnumType("Kind", CType.Int, new List<EnumMember> { new("x", 0), new("y", 1) });
        var ex = Should.Throw<IrUnsupportedException>(() =>
            ir.RegisterEnumType("Kind", CType.Int, new List<EnumMember> { new("a", 0), new("b", 1), new("c", 2) }));
        ex.Message.ShouldContain("two different enums are both named 'Kind'");
    }

    [Fact]
    public void An_enum_re_registered_with_another_tag_type_throws()
    {
        var ir = NewBuilder();
        ir.RegisterEnumType("Kind", CType.Int, new List<EnumMember> { new("x", 0) });
        Should.Throw<IrUnsupportedException>(() =>
            ir.RegisterEnumType("Kind", CType.UChar, new List<EnumMember> { new("x", 0) }));
    }

    [Fact]
    public void A_conflicting_struct_re_registration_throws()
    {
        var ir = NewBuilder();
        ir.RegisterStructType("P", new List<StructField> { new("x", CType.Int) }, isUnion: false);
        ir.RegisterStructType("P", new List<StructField> { new("x", CType.Int) }, isUnion: false);   // identical: fine
        var ex = Should.Throw<IrUnsupportedException>(() =>
            ir.RegisterStructType("P", new List<StructField> { new("y", CType.Long) }, isUnion: false));
        ex.Message.ShouldContain("two different aggregates are both named 'P'");
    }
}

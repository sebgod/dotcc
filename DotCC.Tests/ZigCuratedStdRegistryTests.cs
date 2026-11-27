using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>The curated-std registry is the burn-down ledger of #231 (rules 2 and 3, #233): every row must name the
/// condition that retires it. The validation is one method run over the real table and over a stand-in bad row, so the
/// test is shown to bite.</summary>
public sealed partial class ZigCuratedStdRegistryTests
{
    private static List<string> Validate(IEnumerable<CuratedStdEntry> rows) => ZigCuratedStd.Validate(rows);

    /// <summary>The real table passes the validation.</summary>
    [Fact]
    public void EveryRowNamesItsRetirementCondition()
    {
        Validate(ZigCuratedStd.Rows).ShouldBeEmpty();
        ZigCuratedStd.Rows.Count.ShouldBeGreaterThan(0);
        ZigCuratedStd.ScaffoldCount.ShouldBe(ZigCuratedStd.Rows.Count(r => r.Kind == CuratedKind.Scaffold));
    }

    /// <summary>The validation rejects a row with no retirement condition, an uncited one and a duplicate.</summary>
    [Fact]
    public void ValidationRejectsABadRow()
    {
        var bad = new CuratedStdEntry("std.mem.bad", CuratedShape.NamespaceFn, CuratedKind.Scaffold, "  ");
        Validate([bad]).ShouldContain(p => p.Contains("no retirement condition"));
        var uncited = bad with { RetiresWith = "someday" };
        Validate([uncited]).ShouldNotBeEmpty();
        var floorWithoutS8 = bad with { Kind = CuratedKind.PlatformFloor, RetiresWith = "G2" };
        Validate([floorWithoutS8]).ShouldContain(p => p.Contains("platform floor"));
        var ok = bad with { RetiresWith = "G2 / #172" };
        Validate([ok, ok]).ShouldContain(p => p.Contains("duplicate"));
    }

    /// <summary>Each shape has the factory or lowering it needs, so a dispatch never meets an empty row.</summary>
    [Fact]
    public void RowsCarryWhatTheirShapeNeeds()
    {
        foreach (var row in ZigCuratedStd.Rows)
        {
            switch (row.Shape)
            {
                case CuratedShape.Type: row.MakeType.ShouldNotBeNull(row.Path); break;
                case CuratedShape.GenericType: row.MakeGeneric.ShouldNotBeNull(row.Path); break;
                case CuratedShape.Value: row.Alloc.ShouldNotBeNull(row.Path); break;
                case CuratedShape.NamespaceFn:
                    row.Lower.ShouldNotBeNull(row.Path);
                    row.Arity.ShouldBeGreaterThan(0, row.Path);
                    break;
                case CuratedShape.StaticFn:
                case CuratedShape.Method:
                case CuratedShape.Field:
                case CuratedShape.DeclLiteral:
                    row.LowerMember.ShouldNotBeNull(row.Path);
                    break;
            }
        }
    }

    /// <summary>Every member row (static function, method, field, decl literal) hangs under a curated type row, and
    /// the members the call sites dispatch on are all there: no member-name list lives outside the registry.</summary>
    [Fact]
    public void MemberRowsHangUnderCuratedTypes()
    {
        var typePaths = ZigCuratedStd.Rows
            .Where(r => r.Shape is CuratedShape.Type or CuratedShape.GenericType)
            .Select(r => r.Path)
            .ToHashSet();
        var members = ZigCuratedStd.Rows.Where(r => r.LowerMember is not null).ToList();
        members.ShouldNotBeEmpty();
        foreach (var row in members)
        {
            typePaths.ShouldContain(row.Namespace, row.Path);
            row.Kind.ShouldBe(CuratedKind.Scaffold, row.Path);
        }
        foreach (var path in new[]
        {
            "std.ArrayList.append", "std.ArrayList.items", "std.ArrayList.capacity", "std.ArrayList.empty",
            "std.mem.Allocator.alloc", "std.mem.Allocator.rawFree",
            "std.heap.FixedBufferAllocator.init", "std.heap.FixedBufferAllocator.allocator",
            "std.heap.ArenaAllocator.init", "std.heap.ArenaAllocator.allocator", "std.heap.ArenaAllocator.deinit",
            "std.mem.Alignment.fromByteUnits", "std.mem.Alignment.of", "std.mem.Alignment.toByteUnits",
            "std.mem.Alignment.forward", "std.mem.Alignment.backward", "std.mem.Alignment.check",
        })
        {
            ZigCuratedStd.Find(path).ShouldNotBeNull(path);
        }
        ZigCuratedStd.Find("std.ArrayList.items", CuratedShape.Field).ShouldNotBeNull();
        ZigCuratedStd.Find("std.ArrayList.empty", CuratedShape.DeclLiteral).ShouldNotBeNull();
        ZigCuratedStd.IsOwnedPath("std.ArrayList.append").ShouldBeFalse();
    }

    /// <summary>The report text (#233): the summary line's format, its counts equal to the table's, and the ledger listing
    /// every scaffold row once under its retirement condition.</summary>
    [Fact]
    public void ReportText_summary_line_and_ledger_match_the_table()
    {
        var text = ZigCuratedStd.ReportText();
        var rows = ZigCuratedStd.Rows;
        var scaffold = rows.Count(r => r.Kind == CuratedKind.Scaffold);
        var floor = rows.Count(r => r.Kind == CuratedKind.PlatformFloor);

        var match = Regex.Match(text, @"^curated     : (\d+) rows \((\d+) scaffold, (\d+) platform floor\)$", RegexOptions.Multiline);
        match.Success.ShouldBeTrue(text);
        int.Parse(match.Groups[1].Value).ShouldBe(rows.Count);
        int.Parse(match.Groups[2].Value).ShouldBe(scaffold);
        int.Parse(match.Groups[3].Value).ShouldBe(floor);
        (scaffold + floor).ShouldBe(rows.Count);
        ZigCuratedStd.ScaffoldCount.ShouldBe(scaffold);

        var ledger = text[text.IndexOf("## Curated std ledger (scaffold rows by retirement condition)", StringComparison.Ordinal)..];
        foreach (var row in rows)
        {
            var listed = Regex.Count(ledger, "^  " + Regex.Escape(row.Path) + "$", RegexOptions.Multiline);
            listed.ShouldBe(row.Kind == CuratedKind.Scaffold ? 1 : 0, row.Path);
        }
        foreach (var condition in rows.Where(r => r.Kind == CuratedKind.Scaffold).Select(r => r.RetiresWith).Distinct())
        {
            ledger.ShouldContain(condition);
        }
    }
}

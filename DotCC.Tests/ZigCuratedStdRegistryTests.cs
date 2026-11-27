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
    private static readonly Regex GoalOrIssue = new(@"\b(G[1-5]|S8)\b|#\d+", RegexOptions.CultureInvariant);

    /// <summary>The problems with <paramref name="rows"/>: a blank or uncited retirement condition, a kind that cites the
    /// wrong thing (a scaffold cites a G-goal or an issue, a platform floor cites S8 or #167), a duplicate path.</summary>
    private static List<string> Validate(IEnumerable<CuratedStdEntry> rows)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Path)) { problems.Add($"{row.Path}: duplicate path"); }
            if (!Enum.IsDefined(row.Kind)) { problems.Add($"{row.Path}: no kind"); }
            if (string.IsNullOrWhiteSpace(row.RetiresWith))
            {
                problems.Add($"{row.Path}: no retirement condition");
                continue;
            }
            if (!GoalOrIssue.IsMatch(row.RetiresWith)) { problems.Add($"{row.Path}: the retirement condition cites no G1-G5, S8 or issue"); }
            if (row.Kind == CuratedKind.PlatformFloor && !Regex.IsMatch(row.RetiresWith, @"\bS8\b|#167"))
            {
                problems.Add($"{row.Path}: a platform floor row cites S8 or #167");
            }
            if (row.Kind == CuratedKind.Scaffold && !Regex.IsMatch(row.RetiresWith, @"\bG[1-5]\b|#\d+"))
            {
                problems.Add($"{row.Path}: a scaffold row cites a G-goal or an issue");
            }
        }
        return problems;
    }

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
            }
        }
    }
}

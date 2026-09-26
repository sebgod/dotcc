#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace DotCC.FunctionalTests;

/// <summary>
/// Row-level sharding of the data-driven theories across several test HOSTS, for
/// <c>Scripts/run-functional-sharded.sh</c>. xunit runs the rows of one theory serially inside one
/// class, so the zig oracle's few hundred programs are a single-threaded tail however many cores the
/// box has; a class or method filter cannot split them, since they share one method. With
/// <c>DOTCC_TEST_SHARD=i/n</c> set, each theory keeps only the rows whose index is <c>i</c> modulo
/// <c>n</c>, so <c>n</c> hosts each run a disjoint slice and together run every row once. Separate
/// processes also sidestep the process-global <c>Console</c> redirect (<see cref="FixtureRunner"/>
/// serializes it within one host). Unset (CI, a plain <c>dotnet test</c>), every row runs.
/// </summary>
internal static class TestShard
{
    /// <summary>The environment variable naming this host's shard, as <c>index/count</c>.</summary>
    internal const string Env = "DOTCC_TEST_SHARD";

    private static readonly (int Index, int Count)? Current = Parse(Environment.GetEnvironmentVariable(Env));

    /// <summary>The rows of <paramref name="rows"/> this host runs: all of them when no shard is set. A theory with
    /// fewer rows than there are shards is refused loudly: some host would get none, and xunit reports a theory
    /// without data as a failure (its <c>SkipTestWithoutData</c> skip is still counted as failed, and the runner
    /// then loses track of the test case). Shard fewer ways, or leave a small theory unsharded.</summary>
    internal static IEnumerable<object[]> Rows(IEnumerable<object[]> rows)
    {
        if (Current is not { } shard) { return rows; }
        var all = rows.ToList();
        if (all.Count < shard.Count)
        {
            throw new InvalidOperationException(
                $"{Env}={shard.Index}/{shard.Count}: a sharded theory has only {all.Count} rows, fewer than the shard count");
        }
        return all.Where((_, i) => i % shard.Count == shard.Index);
    }

    /// <summary>Parse <c>index/count</c>; a malformed value fails loudly rather than silently running every row
    /// (or none) on every host.</summary>
    private static (int, int)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var parts = value.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out var index) && int.TryParse(parts[1], out var count)
            && count > 0 && index >= 0 && index < count)
        {
            return (index, count);
        }
        throw new InvalidOperationException($"{Env}='{value}' is not 'index/count' with 0 <= index < count");
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotCC.Frontends;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// The wall-finder (road-to-zig-std.md, milestone S0): an opt-in probe that walks
/// the pinned zig <c>lib/std</c>, attempts to <b>parse</b> (grammar-accept only —
/// no lowering) every <c>.zig</c> file with dotcc's generated Zig grammar, and
/// emits a ranked report of which construct the parser chokes on first and in how
/// many files. It is the campaign's progress bar — "N% of std files parse" — and
/// the data-driven replacement for guessing the S9 surface-debt worklist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parse-only, on purpose.</b> S0 asks a single question — does the grammar
/// <i>accept</i> the file? — so it stops before <c>ZigLowering</c> (which would
/// fail on a hundred not-yet-built constructs and conflate parse gaps with
/// lowering gaps). The engine is <see cref="ZigParseProbe"/> in the library;
/// this test owns the file walk, the toolchain discovery, and the ranking.
/// </para>
/// <para>
/// <b>Opt-in + std discovery</b> (env vars, matching the oracles' posture):
/// gated on <c>DOTCC_RUN_STD_PROBE=1</c>; the std dir comes from
/// <c>DOTCC_ZIG_LIB_DIR</c> (the zig <i>lib</i> dir — real zig's
/// <c>--zig-lib-dir</c> shape; std is its <c>std/</c> subdir) or, failing that, is
/// auto-discovered by parsing <c>zig env</c>'s <c>.std_dir</c>. Both a missing
/// opt-in and a missing toolchain are a clean skip, never a failure. Std is NOT
/// vendored (9 MB) — the probe points at the installed copy, pinned in CI the way
/// the zig oracle already pins <c>0.17.0-dev.667</c>.
/// </para>
/// <para>
/// <b>Not a gate.</b> The probe reports coverage; it does not assert a threshold
/// (the first report is dominated by trivia — <c>test</c> blocks, <c>@"…"</c>
/// quoted identifiers, missing operators — i.e. very low coverage). The only
/// assertion is that the walk found files at all. A ratcheting coverage floor can
/// be added once coverage is meaningful.
/// </para>
/// </remarks>
public sealed class StdParseProbeTests
{
    private const string RunProbeEnv = "DOTCC_RUN_STD_PROBE";
    private const string LibDirEnv = "DOTCC_ZIG_LIB_DIR";
    private const string OutEnv = "DOTCC_STD_PROBE_OUT";

    private readonly ITestOutputHelper _out;

    public StdParseProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_zig_std_parse_coverage()
    {
        if (Environment.GetEnvironmentVariable(RunProbeEnv) != "1")
        {
            Assert.Skip(
                $"Std parse-probe is opt-in. Set {RunProbeEnv}=1 (and optionally " +
                $"{LibDirEnv}=<zig lib dir>, else `zig` on PATH) to walk the pinned " +
                $"lib/std and rank the grammar's parse gaps.");
        }

        var stdDir = ResolveStdDir(out var how);
        if (stdDir is null)
        {
            Assert.Skip(
                $"{RunProbeEnv}=1 but couldn't locate the zig std dir. Set {LibDirEnv} " +
                $"to the zig lib dir (its std/ subdir), or put `zig` on PATH so `zig env` resolves it.");
        }

        var files = Directory
            .EnumerateFiles(stdDir, "*.zig", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var ok = 0;
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string source;
            try { source = File.ReadAllText(file); }
            catch { continue; } // unreadable file — not a parse fact, skip it

            var result = ZigParseProbe.TryParse(source);
            if (result.Status == ZigParseStatus.Ok) { ok++; continue; }

            var key = Signature(result.Status, result.Message);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket(key, result.Message ?? "(no message)");
                buckets[key] = bucket;
            }
            bucket.Count++;
            bucket.Example ??= Relative(stdDir, file);
        }

        var report = BuildReport(stdDir, how, files.Count, ok, buckets);

        var outPath = Environment.GetEnvironmentVariable(OutEnv);
        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = Path.Combine(Path.GetTempPath(), "dotcc-std-parse-probe.txt");
        }
        File.WriteAllText(outPath, report);

        _out.WriteLine(report);
        _out.WriteLine($"(full report written to {outPath})");

        // The only assertion: the walk actually found std source. Coverage is
        // reported, not gated — see the class remarks.
        files.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>One normalized parse-failure signature and how many files hit it.</summary>
    private sealed class Bucket
    {
        public Bucket(string key, string exampleMessage)
        {
            Key = key;
            ExampleMessage = exampleMessage;
        }

        public string Key { get; }
        /// <summary>The full (un-normalized) message from the first file in this bucket —
        /// keeps the <c>expected one of: …</c> context the key strips.</summary>
        public string ExampleMessage { get; }
        public int Count { get; set; }
        public string? Example { get; set; }
    }

    /// <summary>Strips the file-specific location so messages that differ only by
    /// where they fired collapse into one bucket.</summary>
    private static readonly Regex LocRe =
        new(@"\s*at line \d+, column \d+ \(byte offset \d+\)", RegexOptions.Compiled);

    /// <summary>Normalize a raw diagnostic into a stable bucket key: drop the
    /// line/column/offset, and for a parse error drop the (long, context-specific)
    /// <c>expected one of: …</c> tail — the <c>unexpected '&lt;sym&gt;' … in state
    /// &lt;N&gt;</c> head is the construct signature.</summary>
    private static string Signature(ZigParseStatus status, string? message)
    {
        var s = LocRe.Replace(message ?? "(no message)", "");
        switch (status)
        {
            case ZigParseStatus.ParseError:
                var cut = s.IndexOf("; expected one of", StringComparison.Ordinal);
                if (cut >= 0) { s = s[..cut]; }
                return "parse: " + s.Trim();
            case ZigParseStatus.LexError:
                return "lex: " + s.Trim();
            default:
                return "other: " + s.Trim();
        }
    }

    private static string Relative(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return rel.Replace('\\', '/'); // stable across OSes in the report
    }

    private static string BuildReport(
        string stdDir, string how, int total, int ok, Dictionary<string, Bucket> buckets)
    {
        var failing = total - ok;
        var pct = total == 0 ? 0.0 : 100.0 * ok / total;
        var sb = new StringBuilder();
        sb.Append("# dotcc Zig-std parse-probe (road-to-zig-std.md S0)\n");
        sb.Append($"std dir : {stdDir}  (found via {how})\n");
        sb.Append($"grammar : DotCC.Lib/zig.lalr.yaml\n");
        sb.Append($"files   : {total}\n");
        sb.Append($"parse-clean : {ok} ({pct:F1}%)\n");
        sb.Append($"failing     : {failing}\n");
        sb.Append('\n');
        sb.Append("## Parse-failure buckets (ranked by file count) — the S9 worklist\n");
        sb.Append("count  signature\n");
        foreach (var b in buckets.Values
                     .OrderByDescending(b => b.Count)
                     .ThenBy(b => b.Key, StringComparer.Ordinal))
        {
            sb.Append($"{b.Count,5}  {b.Key}\n");
            sb.Append($"       e.g. std/{b.Example}\n");
            // The full first-seen message keeps the `expected one of: …` context that
            // the bucket key drops — invaluable when picking the next brick.
            sb.Append($"       msg: {b.ExampleMessage}\n");
        }
        return sb.ToString();
    }

    // ---- toolchain discovery (Process.Start confined to this opt-in test, the
    //      same posture as the zig oracle; never in the library) ----------------

    /// <summary>Locate the zig std dir. Priority: <c>DOTCC_ZIG_LIB_DIR</c> (a zig
    /// <i>lib</i> dir → its <c>std/</c>, or the dir itself if it already IS std),
    /// then <c>zig env</c>'s <c>.std_dir</c>. Returns null if neither resolves to an
    /// existing directory.</summary>
    private static string? ResolveStdDir(out string how)
    {
        var libDir = Environment.GetEnvironmentVariable(LibDirEnv);
        if (!string.IsNullOrWhiteSpace(libDir))
        {
            var candidateStd = Path.Combine(libDir, "std");
            if (Directory.Exists(candidateStd)) { how = $"{LibDirEnv}=<lib>/std"; return candidateStd; }
            if (Directory.Exists(libDir)) { how = $"{LibDirEnv} (used as-is)"; return libDir; }
        }

        var fromEnv = StdDirFromZigEnv();
        if (fromEnv is not null && Directory.Exists(fromEnv)) { how = "zig env .std_dir"; return fromEnv; }

        how = "unresolved";
        return null;
    }

    private static readonly Regex StdDirRe =
        new(@"\.std_dir\s*=\s*""((?:\\.|[^""\\])*)""", RegexOptions.Compiled);

    /// <summary>Run <c>zig env</c> and pull <c>.std_dir</c> out of its ZON output,
    /// un-escaping the backslash escapes ZON uses on Windows paths. Null if zig
    /// isn't on PATH or the field is absent.</summary>
    private static string? StdDirFromZigEnv()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zig",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("env");
            using var proc = Process.Start(psi);
            if (proc is null) { return null; }
            var outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0) { return null; }

            var m = StdDirRe.Match(outp);
            if (!m.Success) { return null; }
            // ZON escapes: `\\` → `\`, `\"` → `"`. A no-op on Linux (forward slashes).
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }
        catch
        {
            return null;
        }
    }
}

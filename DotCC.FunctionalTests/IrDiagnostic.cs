#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DotCC;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// TEMPORARY diagnostic (not a real gate): runs every fixture through the
/// <c>--ir</c> backend and writes a categorized report so the migration can be
/// prioritized by failure frequency. Delete once the IR path reaches parity.
/// Run with: <c>dotnet test --filter Ir_diagnostic_report</c>.
/// </summary>
public sealed class IrDiagnostic
{
    [Fact]
    public void Ir_diagnostic_report()
    {
        var pass = new List<string>();
        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Bucket(string key, string name)
        {
            if (!buckets.TryGetValue(key, out var l)) { buckets[key] = l = new(); }
            l.Add(name);
        }
        // Once a fixture hangs, its orphaned thread holds FixtureRunner's console
        // lock forever, so every later fixture would block on it. Skip running the
        // rest (they're recorded as blocked) — fix the one HANG and re-run.
        var lockPoisoned = false;

        foreach (var f in FixtureRunner.Discover().OrderBy(f => f.name, StringComparer.Ordinal))
        {
            string emitted;
            try
            {
                emitted = Compiler.EmitCSharp(
                    inputPaths: f.sources,
                    includeDirs: new[] { f.dir },
                    defines: null,
                    fileBased: false,
                    dialect: CDialect.Parse(f.std));
            }
            catch (DotCC.Ir.IrUnsupportedException ex)
            {
                Bucket("UNSUPPORTED: " + ex.Message.Replace("--ir backend does not yet support: ", ""), f.name);
                continue;
            }
            catch (CompileException ex)
            {
                var msg = ex.Message.Split('\n')[0];
                Bucket("COMPILE: " + Trunc(msg), f.name);
                continue;
            }
            catch (Exception ex)
            {
                Bucket("EMIT-EX: " + ex.GetType().Name + ": " + Trunc(ex.Message.Split('\n')[0]), f.name);
                continue;
            }

            // Compile + run on a worker thread with a timeout: a miscompiled
            // fixture can infinite-loop at runtime, which would otherwise hang the
            // whole in-process diagnostic. On timeout the thread is orphaned (it
            // holds FixtureRunner's console lock, so later fixtures cascade to
            // HANG too — the FIRST alphabetical HANG is the real culprit).
            if (lockPoisoned) { Bucket("SKIPPED (after an earlier HANG)", f.name); continue; }
            string? stdout = null;
            Exception? threadEx = null;
            var worker = new Thread(() =>
            {
                try { stdout = FixtureRunner.CompileAndRun(emitted, Array.Empty<string>()); }
                catch (Exception e) { threadEx = e; }
            }) { IsBackground = true };
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(6)))
            {
                Bucket("HANG (runtime loop / blocked)", f.name);
                lockPoisoned = true;
                continue;
            }
            if (threadEx is InvalidOperationException ioe && ioe.Message.StartsWith("Roslyn rejected"))
            {
                var firstErr = ioe.Message.Split('\n').Skip(1).FirstOrDefault(l => l.Contains("error CS")) ?? "";
                Bucket("ROSLYN: " + Trunc(firstErr), f.name);
            }
            else if (threadEx is not null)
            {
                Bucket("RUNTIME-EX: " + threadEx.GetType().Name + ": " + Trunc(threadEx.Message.Split('\n')[0]), f.name);
            }
            else
            {
                var got = (stdout ?? "").ReplaceLineEndings("\n").TrimEnd('\n');
                var want = f.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n');
                if (got == want) { pass.Add(f.name); }
                else { Bucket("STDOUT-MISMATCH", f.name); }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== IR coverage: {pass.Count} PASS / {pass.Count + buckets.Sum(b => b.Value.Count)} total ===");
        sb.AppendLine();
        sb.AppendLine("PASS (" + pass.Count + "): " + string.Join(", ", pass));
        sb.AppendLine();
        foreach (var b in buckets.OrderByDescending(b => b.Value.Count))
        {
            sb.AppendLine($"[{b.Value.Count}] {b.Key}");
            sb.AppendLine("      " + string.Join(", ", b.Value));
        }

        var outPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ir-diagnostic.txt");
        File.WriteAllText(Path.GetFullPath(outPath), sb.ToString());
        // Also surface in test output.
        Console.WriteLine(sb.ToString());
    }

    private static string Trunc(string s) => s.Length > 100 ? s[..100] : s;

    /// <summary>FAST tight-loop probe: only emits C# (parse → IR → codegen), no
    /// Roslyn compile/run, so it returns in a couple of seconds. Buckets the
    /// next blocking unsupported node / compile error per fixture. Use this while
    /// growing node coverage; run the full <see cref="Ir_diagnostic_report"/>
    /// (which also compiles + runs) at checkpoints to catch codegen bugs.
    /// Run with: <c>dotnet test --filter Ir_emit_probe</c>.</summary>
    [Fact]
    public void Ir_emit_probe()
    {
        var ok = new List<string>();
        var buckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Bucket(string key, string name)
        {
            if (!buckets.TryGetValue(key, out var l)) { buckets[key] = l = new(); }
            l.Add(name);
        }
        foreach (var f in FixtureRunner.Discover().OrderBy(f => f.name, StringComparer.Ordinal))
        {
            try
            {
                Compiler.EmitCSharp(f.sources, new[] { f.dir }, null, fileBased: false, dialect: CDialect.Parse(f.std));
                ok.Add(f.name);
            }
            catch (DotCC.Ir.IrUnsupportedException ex)
            {
                Bucket("UNSUPPORTED: " + ex.Message.Replace("--ir backend does not yet support: ", ""), f.name);
            }
            catch (CompileException ex) { Bucket("COMPILE: " + Trunc(ex.Message.Split('\n')[0]), f.name); }
            catch (Exception ex) { Bucket("EMIT-EX: " + ex.GetType().Name + ": " + Trunc(ex.Message.Split('\n')[0]), f.name); }
        }
        var sb = new StringBuilder();
        sb.AppendLine($"=== IR emit: {ok.Count} EMIT-OK / {ok.Count + buckets.Sum(b => b.Value.Count)} total ===");
        sb.AppendLine();
        foreach (var b in buckets.OrderByDescending(b => b.Value.Count))
        {
            sb.AppendLine($"[{b.Value.Count}] {b.Key}");
            sb.AppendLine("      " + string.Join(", ", b.Value));
        }
        File.WriteAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ir-emit-probe.txt")), sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}

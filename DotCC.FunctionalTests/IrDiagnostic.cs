#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            try
            {
                var stdout = FixtureRunner.CompileAndRun(emitted, Array.Empty<string>());
                var got = stdout.ReplaceLineEndings("\n").TrimEnd('\n');
                var want = f.expectedStdout.ReplaceLineEndings("\n").TrimEnd('\n');
                if (got == want) { pass.Add(f.name); }
                else { Bucket("STDOUT-MISMATCH", f.name); }
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Roslyn rejected"))
            {
                var firstErr = ex.Message.Split('\n').Skip(1).FirstOrDefault(l => l.Contains("error CS")) ?? "";
                Bucket("ROSLYN: " + Trunc(firstErr), f.name);
            }
            catch (Exception ex)
            {
                Bucket("RUNTIME-EX: " + ex.GetType().Name + ": " + Trunc(ex.Message.Split('\n')[0]), f.name);
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
}

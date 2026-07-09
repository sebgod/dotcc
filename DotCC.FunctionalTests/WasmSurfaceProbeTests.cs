#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DotCC;
using DotCC.Wasm;
using Shouldly;
using Xunit;

namespace DotCC.FunctionalTests;

/// <summary>
/// The WF0 surface probe (fable-wasm.md): an opt-in inventory of real binary
/// <c>.wasm</c> modules, run <i>before</i> the lifter exists — the S0 wall-finder
/// lesson applied to the wasm campaign. Two corpora feed one committed report:
/// <b>(a)</b> dotcc's own wat-backend corpus (every C program in
/// <see cref="WatOracleTests"/>' inline theories, emitted via
/// <see cref="Compiler.EmitWat"/> and assembled with <c>wat2wasm</c>), and
/// <b>(b)(c)</b> the committed producer modules under <c>WasmProbeModules/</c>
/// (Embedded Swift, clang, zig — see that directory's README for exact builds).
/// </summary>
/// <remarks>
/// <para><b>Opt-in</b> (the oracles' posture): gated on
/// <c>DOTCC_RUN_WASM_PROBE=1</c>; skips cleanly when wabt's <c>wat2wasm</c> isn't
/// on PATH. <see cref="Process.Start"/> stays confined to this env-gated leg.
/// The report lands at <c>DOTCC_WASM_PROBE_OUT</c> (else a temp file); the
/// committed snapshot is <c>docs/plans/wasm-surface-probe.report.txt</c> — each
/// campaign milestone regenerates and re-commits it, so <c>git diff</c> on the
/// report is the progress log (the std-probe pattern).</para>
/// <para><b>Not a gate.</b> The report answers WF0's questions (which post-MVP
/// features real producers emit, whether the Swift runtime is bundled or
/// imported, how big the import surface is, whether export names are usable);
/// the only assertions are that both corpora were found and every module the
/// wat backend emits probes clean.</para>
/// </remarks>
public sealed class WasmSurfaceProbeTests
{
    private const string RunEnv = "DOTCC_RUN_WASM_PROBE";
    private const string OutEnv = "DOTCC_WASM_PROBE_OUT";

    private readonly ITestOutputHelper _out;

    public WasmSurfaceProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_wasm_surface_of_wat_corpus_and_producer_modules()
    {
        if (Environment.GetEnvironmentVariable(RunEnv) != "1")
        {
            Assert.Skip(
                $"Wasm surface-probe is opt-in. Set {RunEnv}=1 (needs wabt's wat2wasm on " +
                $"PATH for the wat-corpus half) to inventory real modules and write the " +
                $"WF0 report.");
        }

        // ---- corpus (a): dotcc's own wat output, assembled ----------------------
        var watSources = WatCorpusSources().ToList();
        var watReports = new List<(string Snippet, WasmProbeReport Report)>();
        foreach (var source in watSources)
        {
            watReports.Add((Snippet(source), ProbeWatProgram(source)));
        }

        // ---- corpora (b)(c): the committed producer modules ---------------------
        var modulesDir = Path.Combine(AppContext.BaseDirectory, "WasmProbeModules");
        var producerReports = new List<(string FileName, long Size, WasmProbeReport Report)>();
        foreach (var file in Directory.EnumerateFiles(modulesDir, "*.wasm").OrderBy(p => p, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            producerReports.Add((Path.GetFileName(file), bytes.Length, WasmModuleProbe.Probe(bytes)));
        }

        var report = BuildReport(watReports, producerReports);

        var outPath = Environment.GetEnvironmentVariable(OutEnv);
        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = Path.Combine(Path.GetTempPath(), "dotcc-wasm-surface-probe.txt");
        }
        File.WriteAllText(outPath, report);
        _out.WriteLine(report);
        _out.WriteLine($"(full report written to {outPath})");

        // Both corpora were actually found…
        watReports.Count.ShouldBeGreaterThan(0);
        producerReports.Count.ShouldBeGreaterThan(0);
        // …and everything our own backend emits must probe clean end to end (the
        // reader's first mechanical validation against real toolchain output).
        foreach (var (snippet, r) in watReports)
        {
            r.Status.ShouldBe(WasmProbeStatus.Ok, $"wat-corpus module failed to probe: {snippet}");
        }
    }

    // ---- corpus (a) plumbing ---------------------------------------------------

    /// <summary>Harvest every inline C program from <see cref="WatOracleTests"/>' two
    /// theories, straight off the <see cref="InlineDataAttribute"/> metadata — the
    /// corpus stays single-sourced with zero duplication and zero new fixtures.</summary>
    private static IEnumerable<string> WatCorpusSources()
    {
        var methods = new[]
        {
            nameof(WatOracleTests.Wat_program_returns_expected_value),
            nameof(WatOracleTests.Wat_program_writes_expected_stdout),
        };
        foreach (var name in methods)
        {
            var method = typeof(WatOracleTests).GetMethod(name);
            method.ShouldNotBeNull($"WatOracleTests.{name} is the probe's corpus source");
            foreach (var attr in method.GetCustomAttributesData())
            {
                if (attr.AttributeType != typeof(InlineDataAttribute)) { continue; }
                if (attr.ConstructorArguments is [{ Value: IReadOnlyList<CustomAttributeTypedArgument> args }] &&
                    args is [{ Value: string source }, ..])
                {
                    yield return source;
                }
            }
        }
    }

    /// <summary>C source → <see cref="Compiler.EmitWat"/> → <c>wat2wasm</c> → probe.</summary>
    private static WasmProbeReport ProbeWatProgram(string source)
    {
        var stem = Path.Combine(Path.GetTempPath(), $"dotcc-wasmprobe-{Guid.NewGuid():N}");
        string c = stem + ".c", wat = stem + ".wat", wasm = stem + ".wasm";
        File.WriteAllText(c, source);
        try
        {
            File.WriteAllText(wat, Compiler.EmitWat(new[] { c }));
            Exec("wat2wasm", wat, "-o", wasm);
            return WasmModuleProbe.Probe(File.ReadAllBytes(wasm));
        }
        finally
        {
            foreach (var f in new[] { c, wat, wasm }) { try { File.Delete(f); } catch { /* best effort */ } }
        }
    }

    /// <summary>Run a tool; a missing binary skips the test (the oracles' pattern),
    /// a non-zero exit fails it.</summary>
    private static void Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) { psi.ArgumentList.Add(a); }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"'{file}' not found on PATH — install wabt (wat2wasm) to run the wasm surface probe.");
            throw; // unreachable: Assert.Skip throws
        }
        if (proc is not { } p) { throw new InvalidOperationException($"failed to start {file}"); }

        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} exited {p.ExitCode}: {stderr}");
        }
    }

    // ---- report rendering --------------------------------------------------------

    private static string Snippet(string source)
    {
        var flat = source.Replace('\n', ' ');
        return flat.Length <= 60 ? flat : flat[..57] + "...";
    }

    private static string BuildReport(
        List<(string Snippet, WasmProbeReport Report)> watReports,
        List<(string FileName, long Size, WasmProbeReport Report)> producerReports)
    {
        var sb = new StringBuilder();
        sb.Append("# dotcc wasm surface probe (fable-wasm.md WF0)\n");
        // Self-dating like the std-probe report: each committed regeneration says
        // when it was measured, and `git diff` on this file is the progress log.
        sb.Append(CultureInfo.InvariantCulture, $"generated: {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'} (UTC)\n");
        sb.Append("probe    : DotCC.Lib/Wasm/WasmModuleProbe.cs (fail-soft reader skeleton)\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"corpus   : (a) {watReports.Count} modules — dotcc wat backend over the WatOracleTests inline corpus (EmitWat + wat2wasm)\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"           (b)(c) {producerReports.Count} committed producer modules (DotCC.FunctionalTests/WasmProbeModules)\n");
        sb.Append('\n');

        // ---- (a): the aggregate view of what our own backend emits --------------
        sb.Append("## (a) dotcc's own wat corpus — the self-round-trip surface\n");
        var malformed = watReports.Where(r => r.Report.Status != WasmProbeStatus.Ok).ToList();
        sb.Append(CultureInfo.InvariantCulture,
            $"status   : {watReports.Count - malformed.Count} ok, {malformed.Count} malformed\n");
        foreach (var (snippet, r) in malformed)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  MALFORMED: {snippet}\n");
            foreach (var note in r.Notes) { sb.Append(CultureInfo.InvariantCulture, $"    note: {note}\n"); }
        }
        var watFeatures = watReports.SelectMany(r => r.Report.Features).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        sb.Append(CultureInfo.InvariantCulture,
            $"features : {(watFeatures.Count == 0 ? "(none — pure MVP)" : string.Join(", ", watFeatures))}\n");
        var watImports = watReports
            .SelectMany(r => r.Report.Imports)
            .Select(i => $"{i.Module}.{i.Name} ({i.Kind})")
            .Distinct()
            .OrderBy(i => i, StringComparer.Ordinal)
            .ToList();
        sb.Append(CultureInfo.InvariantCulture,
            $"imports  : {(watImports.Count == 0 ? "(none)" : string.Join(", ", watImports))}\n");
        sb.Append('\n');
        sb.Append("opcode histogram — every instruction the wat backend emitted, i.e. the\n");
        sb.Append("exact lift surface WF2/WF3 must cover first (count / modules-using / mnemonic):\n");
        AppendHistogram(sb, watReports.Select(r => r.Report).ToList());
        sb.Append('\n');

        // ---- (b)(c): each producer module in full --------------------------------
        sb.Append("## (b)(c) producer modules\n\n");
        foreach (var (fileName, size, r) in producerReports)
        {
            AppendModule(sb, fileName, size, r);
        }

        // ---- the WF0 questions, answered from the data ---------------------------
        sb.Append("## WF0 questions -> answers (from the measurements above)\n");
        sb.Append("post-MVP features per producer:\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"  dotcc-wat : {(watFeatures.Count == 0 ? "(none)" : string.Join(", ", watFeatures))}\n");
        foreach (var (fileName, _, r) in producerReports)
        {
            var features = r.Features.Count == 0 ? "(none)" : string.Join(", ", r.Features);
            sb.Append(CultureInfo.InvariantCulture, $"  {fileName,-9} : {features}\n");
        }
        var swift = producerReports.Where(p => p.FileName.Contains("swift", StringComparison.Ordinal)).ToList();
        foreach (var (fileName, _, r) in swift)
        {
            var runtimeImports = r.Imports.Where(i => !i.Module.StartsWith("wasi_", StringComparison.Ordinal)).ToList();
            sb.Append(CultureInfo.InvariantCulture,
                $"swift runtime ({fileName}): {r.FunctionCount} functions in-module; " +
                $"{(runtimeImports.Count == 0 ? "no non-WASI imports — the Embedded Swift runtime is BUNDLED (tier T2, not T3)" : "non-WASI imports: " + string.Join(", ", runtimeImports.Select(i => $"{i.Module}.{i.Name}")))}\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"swift exports ({fileName}): {string.Join(", ", r.Exports.Select(e => $"{e.Name} ({e.Kind})"))} — @_expose names survive verbatim\n");
        }
        return sb.ToString();
    }

    private static void AppendModule(StringBuilder sb, string fileName, long size, WasmProbeReport r)
    {
        sb.Append(CultureInfo.InvariantCulture, $"### {fileName} ({size:N0} bytes)\n");
        sb.Append(CultureInfo.InvariantCulture, $"status   : {(r.Status == WasmProbeStatus.Ok ? "ok" : "MALFORMED")}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"sections : {string.Join(", ", r.Sections.Select(s => $"{s.Name}({s.Size})"))}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"counts   : {r.FunctionCount} funcs, {r.TypeCount} types, {r.TableCount} tables, " +
            $"{r.MemoryCount} memories, {r.GlobalCount} globals, {r.ElemCount} elems, {r.DataCount} datas" +
            $"{(r.HasStart ? ", start fn" : "")}{(r.HasNameSection ? ", name section" : "")}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"imports  : {(r.Imports.Count == 0 ? "(none)" : string.Join(", ", r.Imports.Select(i => $"{i.Module}.{i.Name} ({i.Kind})")))}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"exports  : {(r.Exports.Count == 0 ? "(none)" : string.Join(", ", r.Exports.Select(e => $"{e.Name} ({e.Kind})")))}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"features : {(r.Features.Count == 0 ? "(none — pure MVP)" : string.Join(", ", r.Features))}\n");
        if (r.TargetFeatures.Count != 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"target_features: {string.Join(", ", r.TargetFeatures)}\n");
        }
        foreach (var note in r.Notes)
        {
            sb.Append(CultureInfo.InvariantCulture, $"note     : {note}\n");
        }
        sb.Append(CultureInfo.InvariantCulture,
            $"opcodes  : {r.InstructionCount} instructions, {r.Opcodes.Count} distinct mnemonics\n");
        AppendHistogram(sb, new List<WasmProbeReport> { r });
        sb.Append('\n');
    }

    /// <summary>One merged histogram over <paramref name="reports"/>, ranked by total
    /// count: <c>count / modules-using / mnemonic</c>.</summary>
    private static void AppendHistogram(StringBuilder sb, List<WasmProbeReport> reports)
    {
        var totals = new Dictionary<string, (int Count, int Modules)>(StringComparer.Ordinal);
        foreach (var r in reports)
        {
            foreach (var (mnemonic, count) in r.Opcodes)
            {
                totals.TryGetValue(mnemonic, out var t);
                totals[mnemonic] = (t.Count + count, t.Modules + 1);
            }
        }
        foreach (var (mnemonic, t) in totals
                     .OrderByDescending(kv => kv.Value.Count)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{t.Count,7}  {t.Modules,4}  {mnemonic}\n");
        }
    }
}

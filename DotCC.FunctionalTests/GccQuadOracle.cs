#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DotCC.FunctionalTests;

/// <summary>
/// Bit-exact differential oracle for our <see cref="DotCC.Libc.Float128"/>,
/// backed by gcc's <c>long double</c> — which is IEEE-754 binary128 on
/// aarch64 Linux (and any target where <c>__LDBL_MANT_DIG__ == 113</c>). For a
/// batch of operations on binary128 bit patterns, it generates a tiny C
/// program, compiles + runs it once inside WSL, and parses each result back as
/// raw bits. This is the authoritative reference for correctly-rounded
/// binary128 — the role QuadrupleLib couldn't fill (its sqrt was ~5 digits and
/// it mis-converted subnormals/Max).
/// </summary>
/// <remarks>
/// <para>
/// gcc, not libquadmath: on aarch64 there's no <c>&lt;quadmath.h&gt;</c>, but
/// <c>long double</c> already IS binary128, so <c>+ − × ÷</c>, <c>sqrtl</c>,
/// <c>fmal</c>, and the <c>…l</c> transcendentals operate at full quad
/// precision via plain libm. A compile-time <c>#if __LDBL_MANT_DIG__ != 113</c>
/// guard makes the oracle refuse to run on a target where <c>long double</c>
/// is something else (e.g. x86 80-bit extended) rather than silently compare
/// against the wrong format — <see cref="IsAvailable"/> probes for it and the
/// tests skip when it's absent.
/// </para>
/// <para>
/// Bit bridging: a binary128 value is two 64-bit words. We pass operands into
/// C as <c>(hi, lo)</c> literals reassembled with <c>memcpy</c>, and read
/// results back the same way — no float parsing/formatting in the loop, so the
/// comparison is exact.
/// </para>
/// </remarks>
internal static class GccQuadOracle
{
    /// <summary>
    /// A binary128 operation to evaluate. <see cref="Expr"/> is a C expression
    /// template using <c>{0}</c>, <c>{1}</c>, … for the operand placeholders
    /// (each becomes a reconstructed <c>long double</c>). <see cref="Arity"/>
    /// is the operand count; <see cref="ResultIsBinary128"/> selects whether
    /// the result is printed as a 128-bit pattern or a 64-bit double.
    /// </summary>
    internal sealed record Op(int Arity, bool ResultIsBinary128, string Expr);

    /// <summary>binary128 → double narrowing (correctly rounded).</summary>
    internal static readonly Op NarrowToDouble = new(1, ResultIsBinary128: false, "(double)({0})");
    /// <summary>binary128 addition (correctly rounded).</summary>
    internal static readonly Op Add = new(2, ResultIsBinary128: true, "({0}) + ({1})");
    /// <summary>binary128 subtraction (correctly rounded).</summary>
    internal static readonly Op Subtract = new(2, ResultIsBinary128: true, "({0}) - ({1})");
    /// <summary>binary128 multiplication (correctly rounded).</summary>
    internal static readonly Op Multiply = new(2, ResultIsBinary128: true, "({0}) * ({1})");
    /// <summary>binary128 division (correctly rounded).</summary>
    internal static readonly Op Divide = new(2, ResultIsBinary128: true, "({0}) / ({1})");
    /// <summary>binary128 square root (correctly rounded).</summary>
    internal static readonly Op Sqrt = new(1, ResultIsBinary128: true, "sqrtl({0})");
    /// <summary>binary128 fused multiply-add (single rounding).</summary>
    internal static readonly Op Fma = new(3, ResultIsBinary128: true, "fmal({0}, {1}, {2})");
    // Exact integral / algebraic ops (correctly rounded ⇒ bit-exact vs gcc).
    internal static readonly Op Floor = new(1, ResultIsBinary128: true, "floorl({0})");
    internal static readonly Op Ceiling = new(1, ResultIsBinary128: true, "ceill({0})");
    internal static readonly Op Truncate = new(1, ResultIsBinary128: true, "truncl({0})");
    internal static readonly Op Round = new(1, ResultIsBinary128: true, "rintl({0})"); // default mode = ties-even
    internal static readonly Op CopySign = new(2, ResultIsBinary128: true, "copysignl({0}, {1})");
    internal static readonly Op Fmod = new(2, ResultIsBinary128: true, "fmodl({0}, {1})");
    // Correctly-rounded by us, but glibc's aren't, so compared within a ULP
    // tolerance rather than bit-exact.
    internal static readonly Op Cbrt = new(1, ResultIsBinary128: true, "cbrtl({0})");
    internal static readonly Op Hypot = new(2, ResultIsBinary128: true, "hypotl({0}, {1})");
    // Transcendentals — high-precision here, ~correctly rounded; gcc within tolerance.
    internal static readonly Op Exp = new(1, ResultIsBinary128: true, "expl({0})");
    internal static readonly Op Log = new(1, ResultIsBinary128: true, "logl({0})");
    internal static readonly Op Exp2 = new(1, ResultIsBinary128: true, "exp2l({0})");
    internal static readonly Op Exp10 = new(1, ResultIsBinary128: true, "exp10l({0})");
    internal static readonly Op Expm1 = new(1, ResultIsBinary128: true, "expm1l({0})");
    internal static readonly Op Log2 = new(1, ResultIsBinary128: true, "log2l({0})");
    internal static readonly Op Log10 = new(1, ResultIsBinary128: true, "log10l({0})");
    internal static readonly Op Log1p = new(1, ResultIsBinary128: true, "log1pl({0})");
    internal static readonly Op Pow = new(2, ResultIsBinary128: true, "powl({0}, {1})");
    internal static readonly Op Sinh = new(1, ResultIsBinary128: true, "sinhl({0})");
    internal static readonly Op Cosh = new(1, ResultIsBinary128: true, "coshl({0})");
    internal static readonly Op Tanh = new(1, ResultIsBinary128: true, "tanhl({0})");
    internal static readonly Op Asinh = new(1, ResultIsBinary128: true, "asinhl({0})");
    internal static readonly Op Acosh = new(1, ResultIsBinary128: true, "acoshl({0})");
    internal static readonly Op Atanh = new(1, ResultIsBinary128: true, "atanhl({0})");
    internal static readonly Op Sin = new(1, ResultIsBinary128: true, "sinl({0})");
    internal static readonly Op Cos = new(1, ResultIsBinary128: true, "cosl({0})");
    internal static readonly Op Tan = new(1, ResultIsBinary128: true, "tanl({0})");
    internal static readonly Op Atan = new(1, ResultIsBinary128: true, "atanl({0})");
    internal static readonly Op Asin = new(1, ResultIsBinary128: true, "asinl({0})");
    internal static readonly Op Acos = new(1, ResultIsBinary128: true, "acosl({0})");
    internal static readonly Op Atan2 = new(2, ResultIsBinary128: true, "atan2l({0}, {1})");
    internal static readonly Op Remainder = new(2, ResultIsBinary128: true, "remainderl({0}, {1})"); // exact

    /// <summary>
    /// Evaluate a binary128-result op over each case (operand bit patterns) and
    /// return the result bit patterns.
    /// </summary>
    public static UInt128[] ComputeBinary128(Op op, IReadOnlyList<UInt128[]> cases)
    {
        if (!op.ResultIsBinary128)
        {
            throw new ArgumentException("op does not produce a binary128 result", nameof(op));
        }
        var lines = Run(op, cases);
        var result = new UInt128[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var hi = ulong.Parse(lines[i].AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var lo = ulong.Parse(lines[i].AsSpan(16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            result[i] = ((UInt128)hi << 64) | lo;
        }
        return result;
    }

    private static readonly object _initLock = new();
    private static bool _initialised;
    private static bool _available;

    /// <summary>True if WSL has a gcc whose <c>long double</c> is binary128.</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialised(); return _available; }
    }

    /// <summary>
    /// Narrow each binary128 input to <c>double</c> using gcc, returning the
    /// raw <see cref="double"/> bit patterns.
    /// </summary>
    public static ulong[] NarrowToDouble64(IReadOnlyList<UInt128> inputs)
    {
        var cases = new List<UInt128[]>(inputs.Count);
        foreach (var x in inputs) { cases.Add(new[] { x }); }
        var lines = Run(NarrowToDouble, cases);
        var result = new ulong[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            result[i] = ulong.Parse(lines[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>
    /// Evaluate <paramref name="op"/> over every case (each case carries
    /// <see cref="Op.Arity"/> operand bit patterns) and return the raw hex
    /// result line per case (16 hex digits for a double result, 32 for a
    /// binary128 result — high word first).
    /// </summary>
    private static string[] Run(Op op, IReadOnlyList<UInt128[]> cases)
    {
        EnsureInitialised();
        if (!_available)
        {
            throw new InvalidOperationException("gcc binary128 oracle is not available on this host.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-quad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            File.WriteAllText(Path.Combine(workDir, "q.c"), GenerateSource(op, cases));
            var wsl = ToWslPath(workDir);
            var compile = RunWslBash($"cd '{wsl}' && gcc -std=c17 q.c -o q -lm");
            if (compile.exit != 0)
            {
                throw new InvalidOperationException(
                    $"gcc binary128 oracle: compile failed (exit {compile.exit}).\n{compile.stderr}");
            }
            var run = RunWslBash($"cd '{wsl}' && ./q");
            if (run.exit != 0)
            {
                throw new InvalidOperationException(
                    $"gcc binary128 oracle: run failed (exit {run.exit}).\n{run.stderr}");
            }
            var lines = run.stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != cases.Count)
            {
                throw new InvalidOperationException(
                    $"gcc binary128 oracle: expected {cases.Count} result lines, got {lines.Length}.");
            }
            return lines;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string GenerateSource(Op op, IReadOnlyList<UInt128[]> cases)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#define _GNU_SOURCE"); // declares the GNU `exp10l` etc.
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <string.h>");
        sb.AppendLine("#include <math.h>");
        // Refuse to produce a misleading reference if long double isn't binary128.
        sb.AppendLine("#if __LDBL_MANT_DIG__ != 113");
        sb.AppendLine("#error \"long double is not IEEE-754 binary128 on this target\"");
        sb.AppendLine("#endif");
        sb.AppendLine("typedef unsigned long long u64;");
        // Reassemble a long double from (hi, lo) — little-endian limbs.
        sb.AppendLine("static long double ld(u64 hi, u64 lo){ u64 w[2]={lo,hi}; long double x; memcpy(&x,w,16); return x; }");
        sb.AppendLine("int main(void){");
        foreach (var operands in cases)
        {
            var args = new string[operands.Length];
            for (int i = 0; i < operands.Length; i++)
            {
                args[i] = $"ld(0x{(ulong)(operands[i] >> 64):x16}ULL,0x{(ulong)operands[i]:x16}ULL)";
            }
            var expr = string.Format(CultureInfo.InvariantCulture, op.Expr, args);
            if (op.ResultIsBinary128)
            {
                sb.AppendLine($"  {{ long double r = {expr}; u64 w[2]; memcpy(w,&r,16); printf(\"%016llx%016llx\\n\", w[1], w[0]); }}");
            }
            else
            {
                sb.AppendLine($"  {{ double r = {expr}; u64 b; memcpy(&b,&r,8); printf(\"%016llx\\n\", b); }}");
            }
        }
        sb.AppendLine("  return 0;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── WSL plumbing ─────────────────────────────────────────────────────────
    private static string ToWslPath(string windowsPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("wslpath");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(windowsPath.Replace('\\', '/'));
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn wsl.exe for wslpath");
        var outp = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        if (proc.ExitCode != 0 || outp.Length == 0)
        {
            throw new InvalidOperationException($"wslpath failed for '{windowsPath}'.");
        }
        return outp;
    }

    private static (string stdout, string stderr, int exit) RunWslBash(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to spawn wsl.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    private static void EnsureInitialised()
    {
        if (_initialised) { return; }
        lock (_initLock)
        {
            if (_initialised) { return; }
            _initialised = true;
            if (!OperatingSystem.IsWindows()) { return; }
            try
            {
                // Probe: gcc present AND long double is binary128 (mant dig 113).
                var workDir = Path.Combine(Path.GetTempPath(), $"dotcc-quad-probe-{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                try
                {
                    File.WriteAllText(Path.Combine(workDir, "p.c"),
                        "#include <stdio.h>\nint main(void){ printf(\"%d\\n\", (int)__LDBL_MANT_DIG__); return 0; }\n");
                    var wsl = ToWslPath(workDir);
                    var build = RunWslBash($"cd '{wsl}' && gcc -std=c17 p.c -o p && ./p");
                    _available = build.exit == 0 && build.stdout.Replace("\r", "").Trim() == "113";
                }
                finally
                {
                    try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
                }
            }
            catch
            {
                _available = false;
            }
        }
    }
}

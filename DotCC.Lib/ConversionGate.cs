#nullable enable

using System.Collections.Generic;

namespace DotCC;

/// <summary>
/// Collects <c>-Wconversion</c> diagnostics during the emit pass. C allows an
/// implicit integer conversion at a store (assignment / initialization / return)
/// even when it NARROWS — the target type holds fewer bytes than the source
/// (<c>lu_byte b = some_int;</c>). C# requires an explicit cast there, which
/// dotcc inserts (see <c>CoerceStore</c>); this gate additionally records the
/// narrowing so the frontend can warn, mirroring <c>gcc -Wconversion</c> /
/// <c>clang -Wconversion</c> / MSVC C4244. Like those, it is OFF by default —
/// opt-in via <c>-Wconversion</c> — because narrowing diagnostics are noisy.
/// </summary>
/// <remarks>
/// Constructed (and threaded into the emit-pass <see cref="CSharpEmitter"/>) only
/// when <c>-Wconversion</c> is set, so it is a no-op on the default path. Wired
/// into the EMIT pass only — never the analysis pass — so each store is reported
/// once. Identical messages (e.g. the same inlined header store seen across many
/// translation units) are de-duplicated. Severity is fixed at warning; the
/// frontend flushes the list to stderr.
/// </remarks>
internal sealed class ConversionGate
{
    private readonly List<string> _diagnostics = new();
    private readonly HashSet<string> _seen = new(System.StringComparer.Ordinal);

    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool HasAny => _diagnostics.Count > 0;

    /// <summary>
    /// Record a width-narrowing integer conversion from <paramref name="srcType"/>
    /// to <paramref name="tgtType"/> (the C# type names as emitted, e.g. the
    /// alias <c>lu_byte</c> and its target). <paramref name="function"/> is the
    /// enclosing C function (null at file scope); <paramref name="line"/> the
    /// source line (0 = unknown). Duplicate messages are dropped.
    /// </summary>
    public void Narrowing(string srcType, string tgtType, string? function, int line)
    {
        var where = line > 0 ? $" (line {line})" : "";
        var fn = function is not null ? $" in `{function}`" : "";
        var msg = $"implicit conversion from `{srcType}` to `{tgtType}` may lose data{fn}{where} [-Wconversion]";
        if (_seen.Add(msg)) { _diagnostics.Add(msg); }
    }
}

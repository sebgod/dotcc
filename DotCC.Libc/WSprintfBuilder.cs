#nullable enable

using System;
using System.IO;

namespace DotCC.Libc;

/// <summary>
/// Fluent <c>swprintf</c> builder — the wide sibling of <see cref="SprintfBuilder"/>.
/// Wraps a <see cref="PrintfBuilder"/> pointed at a private <see cref="StringWriter"/>
/// (over the wide format transcoded to UTF-8, so the spec grammar is parsed
/// identically); on <see cref="Done"/>, copies the formatted text — as UTF-16 code
/// units, no byte re-encoding — into the destination <c>wchar_t*</c> buffer and
/// NUL-terminates.
/// </summary>
/// <remarks>
/// C's <c>swprintf(s, n, fmt, …)</c> bounds the write to <paramref name="_capacity"/>
/// wide characters <em>including</em> the terminating NUL, and — unlike
/// <c>snprintf</c> — returns a <em>negative</em> value when the result (plus NUL)
/// does not fit, rather than the would-be length.
/// </remarks>
public unsafe ref struct WSprintfBuilder
{
    private PrintfBuilder _inner;
    private readonly StringWriter _buf;
    private readonly char* _dst;
    private readonly int _capacity;   // n — max wide chars incl. the terminating NUL

    internal WSprintfBuilder(char* dst, byte* utf8Fmt, int capacity)
    {
        _dst = dst;
        _capacity = capacity;
        _buf = new StringWriter();
        _inner = new PrintfBuilder(_buf, utf8Fmt);
    }

    // Thin wrapper — every overload delegates to the inner PrintfBuilder. Arg(char*)
    // is the wide %s path; the rest mirror SprintfBuilder. (A missing overload is a
    // latent miscompile, not a compile error — see SprintfBuilder's note.)
    public WSprintfBuilder Arg(int v)      { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(long v)     { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(uint v)     { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(ulong v)    { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(bool v)     { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(double v)   { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(float v)    { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(Float128 v) { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(byte* v)    { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(void* v)    { _inner = _inner.Arg(v); return this; }
    public WSprintfBuilder Arg(char* v)    { _inner = _inner.Arg(v); return this; }

    public int Done()
    {
        _inner.Done();
        // The formatted text is already UTF-16 in the StringWriter — copy the code
        // units straight into the wide buffer (no Latin-1 byte step that the narrow
        // SprintfBuilder needs). %c wrote a (char) value, which lands verbatim.
        var str = _buf.ToString();
        int max = _capacity > 0 ? _capacity - 1 : 0;   // reserve a slot for the NUL
        if (str.Length > max)
        {
            // Doesn't fit (incl. NUL): NUL-terminate what fits and report failure,
            // per C swprintf (negative on overflow — distinct from snprintf).
            for (int i = 0; i < max; i++) { _dst[i] = str[i]; }
            if (_capacity > 0) { _dst[max] = '\0'; }
            return -1;
        }
        for (int i = 0; i < str.Length; i++) { _dst[i] = str[i]; }
        if (_capacity > 0) { _dst[str.Length] = '\0'; }
        return str.Length;
    }
}

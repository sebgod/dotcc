#nullable enable

using System;
using System.IO;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// Fluent <c>sprintf</c> / <c>snprintf</c> builder. Wraps a
/// <see cref="PrintfBuilder"/> pointed at a private <see cref="StringWriter"/>;
/// on <see cref="Done"/>, copies the formatted bytes (UTF-8) into the
/// destination buffer and NUL-terminates.
/// </summary>
/// <remarks>
/// Allocates one <see cref="StringWriter"/> and one <see cref="byte"/>[] per
/// call — unavoidable since the format expansion isn't size-bounded in
/// advance. Real C's sprintf is also "you guess the buffer size"; snprintf
/// caps the copy. The builder reports back the count that <em>would</em>
/// have been written (matches the C standard).
/// </remarks>
public unsafe ref struct SprintfBuilder
{
    private PrintfBuilder _inner;
    private readonly StringWriter _buf;
    private readonly byte* _dst;
    private readonly int _capacity;  // <0 = unbounded sprintf; ≥0 = snprintf bound (exclusive of NUL)

    internal SprintfBuilder(byte* dst, byte* fmt, int capacity)
    {
        _dst = dst;
        _capacity = capacity;
        _buf = new StringWriter();
        _inner = new PrintfBuilder(_buf, fmt);
    }

    // Mirror PrintfBuilder's full Arg surface — SprintfBuilder is a thin wrapper
    // over an inner PrintfBuilder, so every overload simply delegates. Missing an
    // overload here is a latent miscompile, not a compile error: e.g. a `long`
    // would bind to Arg(float) and format an integer as a float.
    public SprintfBuilder Arg(int v)      { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(long v)     { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(uint v)     { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(ulong v)    { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(bool v)     { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(double v)   { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(float v)    { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(Float128 v) { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(byte* v)    { _inner = _inner.Arg(v); return this; }
    public SprintfBuilder Arg(void* v)    { _inner = _inner.Arg(v); return this; }

    public int Done()
    {
        _inner.Done();
        var bytes = Encoding.UTF8.GetBytes(_buf.ToString());
        int writeCount = _capacity < 0 ? bytes.Length : Math.Min(bytes.Length, _capacity);
        for (int i = 0; i < writeCount; i++) { _dst[i] = bytes[i]; }
        _dst[writeCount] = 0;
        // Real sprintf returns total chars that *would* have been written
        // (excl. terminating NUL) — same when fully copied, larger than
        // writeCount when snprintf truncates.
        return bytes.Length;
    }
}

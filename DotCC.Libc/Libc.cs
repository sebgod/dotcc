#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// libc-shaped runtime surface for dotcc-emitted programs. Every function
/// routes to a .NET BCL primitive. Names mirror C verbatim (lower-case) so
/// the emitter can pass identifiers through untouched once the integration
/// lands; capitalised aliases (<see cref="Malloc"/>, <see cref="Free"/>,
/// <see cref="Printf"/>) exist for callers that prefer .NET casing.
/// </summary>
/// <remarks>
/// Unsafe-by-design — every entry takes/returns raw pointers because that's
/// what real C code passes around. The library never allocates a GC object on
/// any hot path (Printf's <see cref="PrintfBuilder"/> is a ref struct,
/// <see cref="L"/> pins RVA data, strlen/strcmp/memset are bare loops).
/// </remarks>
public static unsafe partial class Libc
{
    // ---------------------------------------------------------------------
    // Memory: malloc / free
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>malloc(size)</c> — allocate <paramref name="size"/> bytes from the
    /// process heap. Backed by <see cref="NativeMemory.Alloc(nuint)"/>.
    /// Returns <c>null</c> only on OOM (the underlying syscall throws an
    /// <see cref="OutOfMemoryException"/> instead — match real C by catching
    /// in user code if needed).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* malloc(int size) => NativeMemory.Alloc((nuint)size);

    /// <inheritdoc cref="malloc(int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Malloc(int size) => malloc(size);

    /// <summary>
    /// <c>free(p)</c> — return memory previously returned from
    /// <see cref="malloc"/> to the heap. <c>free(NULL)</c> is a no-op.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void free(void* p) => NativeMemory.Free(p);

    /// <inheritdoc cref="free(void*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Free(void* p) => free(p);

    /// <summary>
    /// <c>memset(dst, value, count)</c> — fill <paramref name="count"/>
    /// bytes at <paramref name="dst"/> with the low byte of
    /// <paramref name="value"/>. Routes to
    /// <see cref="NativeMemory.Fill(void*, nuint, byte)"/>. Returns
    /// <paramref name="dst"/> (matches C signature).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* memset(void* dst, int value, int count)
    {
        NativeMemory.Fill(dst, (nuint)count, (byte)value);
        return dst;
    }

    /// <summary>
    /// <c>memcpy(dst, src, count)</c> — copy <paramref name="count"/> bytes
    /// from <paramref name="src"/> to <paramref name="dst"/>. Routes to
    /// <see cref="Buffer.MemoryCopy(void*, void*, long, long)"/>. Returns
    /// <paramref name="dst"/> (matches C signature).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* memcpy(void* dst, void* src, int count)
    {
        Buffer.MemoryCopy(src, dst, count, count);
        return dst;
    }

    // ---------------------------------------------------------------------
    // C-string ops over byte* (NUL-terminated UTF-8)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strlen(s)</c> — bytes before the first <c>0</c> in
    /// <paramref name="s"/>. UB if <paramref name="s"/> is null (matches C).
    /// </summary>
    public static int strlen(byte* s)
    {
        int n = 0;
        while (s[n] != 0) { n++; }
        return n;
    }

    /// <summary>
    /// <c>strcmp(a, b)</c> — lexicographic byte-by-byte compare. Returns
    /// 0 / negative / positive. Real C returns <c>int</c>; we narrow the
    /// byte difference back to int.
    /// </summary>
    public static int strcmp(byte* a, byte* b)
    {
        while (*a != 0 && *a == *b) { a++; b++; }
        return *a - *b;
    }

    /// <summary>
    /// <c>strcpy(dst, src)</c> — copy NUL-terminated <paramref name="src"/>
    /// into <paramref name="dst"/>, including the terminating <c>0</c>.
    /// Returns <paramref name="dst"/>.
    /// </summary>
    public static byte* strcpy(byte* dst, byte* src)
    {
        byte* p = dst;
        while ((*p++ = *src++) != 0) { }
        return dst;
    }

    // ---------------------------------------------------------------------
    // I/O — streams, fprintf, printf, puts, fputs
    //
    // FILE* in C is opaque; we model it as a managed `TextWriter`. `stdout`
    // and `stderr` are the obvious BCL writers. `fprintf` is the underlying
    // primitive; `printf` is `fprintf(stdout, ...)`. `puts` is
    // `fputs(stdout, s)` plus a newline. This shape mirrors how the actual
    // C standard library factors them.
    // ---------------------------------------------------------------------

    /// <summary>Standard output stream. Maps to <see cref="Console.Out"/>.</summary>
    public static TextWriter stdout => Console.Out;

    /// <summary>Standard error stream. Maps to <see cref="Console.Error"/>.</summary>
    public static TextWriter stderr => Console.Error;

    /// <summary>Standard input stream. Maps to <see cref="Console.In"/>.</summary>
    public static TextReader stdin => Console.In;

    /// <summary>
    /// <c>fprintf(stream, fmt)</c> — the primitive. Start a fluent format
    /// chain that writes to <paramref name="stream"/>. Each
    /// <see cref="PrintfBuilder.Arg(int)"/> / <see cref="PrintfBuilder.Arg(double)"/>
    /// / <see cref="PrintfBuilder.Arg(byte*)"/> consumes the next <c>%</c>
    /// spec. Finish with <see cref="PrintfBuilder.Done()"/> which returns
    /// the printf-style <c>int</c>.
    /// </summary>
    /// <remarks>
    /// Fluent rather than <c>params object[]</c> because raw pointers can't
    /// be boxed. The builder is a <c>ref struct</c> so it stays stack-only
    /// and zero-alloc.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder fprintf(TextWriter stream, byte* fmt) => new(stream, fmt);

    /// <summary><c>printf(fmt)</c> ≡ <c>fprintf(stdout, fmt)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder printf(byte* fmt) => fprintf(stdout, fmt);

    /// <inheritdoc cref="printf(byte*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PrintfBuilder Printf(byte* fmt) => printf(fmt);

    /// <summary>
    /// <c>fputs(s, stream)</c> — write NUL-terminated UTF-8
    /// <paramref name="s"/> to <paramref name="stream"/>. No newline (per
    /// C standard — only <c>puts</c> appends one). Returns a non-negative
    /// byte count on success.
    /// </summary>
    public static int fputs(byte* s, TextWriter stream)
    {
        if (s == null)
        {
            stream.Write("(null)");
            return 6;
        }
        int len = strlen(s);
        stream.Write(System.Text.Encoding.UTF8.GetString(s, len));
        return len;
    }

    /// <summary>
    /// <c>puts(s)</c> ≡ <c>fputs(s, stdout)</c> + newline. Returns
    /// <c>len + newline-bytes</c> on success (matches real C's
    /// "non-negative count on success" contract).
    /// </summary>
    public static int puts(byte* s)
    {
        int n = fputs(s, stdout);
        stdout.WriteLine();
        return n + Environment.NewLine.Length;
    }

    /// <summary>
    /// <c>sprintf(dst, fmt, ...)</c> — format into <paramref name="dst"/>,
    /// NUL-terminated. Returns the number of bytes that <em>would</em> have
    /// been written (excl. NUL) — caller is responsible for sizing
    /// <paramref name="dst"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SprintfBuilder sprintf(byte* dst, byte* fmt) => new(dst, fmt, capacity: -1);

    /// <summary>
    /// <c>snprintf(dst, n, fmt, ...)</c> — like <see cref="sprintf"/> but
    /// caps the copy at <paramref name="n"/> bytes (inclusive of the
    /// terminating NUL — i.e. at most <c>n-1</c> formatted bytes plus NUL).
    /// Return value is the count that would have been written even when
    /// truncated, matching C99.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SprintfBuilder snprintf(byte* dst, int n, byte* fmt) => new(dst, fmt, capacity: Math.Max(0, n - 1));

    /// <summary>
    /// <c>fscanf(stream, fmt)</c> — start a fluent scan chain reading from
    /// <paramref name="stream"/>. Each <see cref="ScanfReader.Read(int*)"/>
    /// / <see cref="ScanfReader.Read(double*)"/> /
    /// <see cref="ScanfReader.Read(byte*)"/> consumes the next <c>%</c>
    /// spec and writes the parsed value. <see cref="ScanfReader.Done"/>
    /// returns the count of successful matches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ScanfReader fscanf(TextReader stream, byte* fmt) => new(stream, fmt);

    /// <summary><c>scanf(fmt)</c> ≡ <c>fscanf(stdin, fmt)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ScanfReader scanf(byte* fmt) => fscanf(stdin, fmt);

    /// <summary>
    /// <c>sscanf(src, fmt)</c> — parse from the NUL-terminated UTF-8
    /// byte buffer <paramref name="src"/> instead of a stream. Equivalent
    /// to wrapping <paramref name="src"/> in a <see cref="StringReader"/>
    /// and calling <see cref="fscanf"/>.
    /// </summary>
    public static ScanfReader sscanf(byte* src, byte* fmt)
    {
        int len = strlen(src);
        var s = System.Text.Encoding.UTF8.GetString(src, len);
        return new ScanfReader(new StringReader(s), fmt);
    }

    // ---------------------------------------------------------------------
    // String literal lowering
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>L(u8)</c> — pin a UTF-8 RVA byte literal's address and return it
    /// as <c>byte*</c>. Used by the emitter to lower C string literals
    /// (<c>"foo"</c>) to a NUL-terminated pointer into the assembly's
    /// read-only data section. No GC pinning required — RVA data lives at a
    /// fixed address for program lifetime.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* L(ReadOnlySpan<byte> u8) =>
        (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(u8));
}

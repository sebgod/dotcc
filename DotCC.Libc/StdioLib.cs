#nullable enable

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;stdio.h&gt;</c> character I/O — the byte-at-a-time read/write
/// family on a <c>FILE*</c> (see FileLib.cs for the <c>FILE</c> model). These
/// route through the FILE's byte helpers (<see cref="Libc.WriteByteTo"/> /
/// <see cref="Libc.ReadByteFrom"/>): byte-exact on a file-backed stream,
/// through <c>Console.*</c> on the std streams.
/// </summary>
/// <remarks>
/// They operate on one byte at a time. To a file-backed stream the raw byte is
/// written or read verbatim (correct for binary). The console streams follow
/// dotcc's UTF-8-in-<c>char*</c> model: bytes written to <c>stdout</c> /
/// <c>stderr</c> go through a per-stream UTF-8 decoder, so a multi-byte
/// sequence written a byte at a time still prints as its character, and each
/// character read from <c>stdin</c> is handed out as its UTF-8 bytes.
/// <c>EOF</c> is <c>-1</c>.
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary><c>fputc(c, stream)</c> — write the byte
    /// <c>(unsigned char)<paramref name="c"/></c> to <paramref name="stream"/>;
    /// return the byte written (as <c>int</c>), or <c>EOF</c> if the write failed
    /// (e.g. a read-only stream — matches C, which sets the error indicator).</summary>
    public static int fputc(int c, FILE* stream)
    {
        if (!WriteByteTo(stream, (byte)c)) { return -1; }   // EOF
        return c & 0xFF;
    }

    /// <summary><c>putc(c, stream)</c> — identical to <see cref="fputc"/> (real C
    /// allows <c>putc</c> to be a multiply-evaluating macro; dotcc makes it a
    /// plain call).</summary>
    public static int putc(int c, FILE* stream) => fputc(c, stream);

    /// <summary><c>putchar(c)</c> ≡ <c>fputc(c, stdout)</c>.</summary>
    public static int putchar(int c) => fputc(c, stdout);

    /// <summary><c>fputs</c> for a single char already lives in Libc.cs; this is
    /// the matching reader. <c>fgetc(stream)</c> — read one byte from
    /// <paramref name="stream"/>; return it (0..255) or <c>EOF</c> (-1) at end of
    /// input.</summary>
    public static int fgetc(FILE* stream) => ReadByteFrom(stream);

    /// <summary><c>getc(stream)</c> — identical to <see cref="fgetc"/>.</summary>
    public static int getc(FILE* stream) => fgetc(stream);

    /// <summary><c>getchar()</c> ≡ <c>fgetc(stdin)</c>.</summary>
    public static int getchar() => fgetc(stdin);

    /// <summary>
    /// <c>fgets(s, n, stream)</c> — read at most <c><paramref name="n"/>-1</c>
    /// bytes from <paramref name="stream"/> into <paramref name="s"/>, stopping
    /// after a newline (which is kept) or at EOF, then NUL-terminate. Returns
    /// <paramref name="s"/>, or <c>null</c> if EOF is hit before any byte is read.
    /// </summary>
    public static byte* fgets(byte* s, int n, FILE* stream)
    {
        if (n <= 0) { return null; }
        int i = 0;
        while (i < n - 1)
        {
            int ch = ReadByteFrom(stream);
            if (ch < 0)
            {
                if (i == 0) { return null; }   // EOF with nothing read
                break;
            }
            s[i++] = (byte)ch;
            if (ch == '\n') { break; }
        }
        s[i] = 0;
        return s;
    }
}

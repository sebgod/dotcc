#nullable enable

using System.IO;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;stdio.h&gt;</c> character I/O — the byte-at-a-time read/write
/// family layered on the same <see cref="System.IO.TextWriter"/> /
/// <see cref="System.IO.TextReader"/> model as <c>fprintf</c> / <c>fscanf</c>
/// (dotcc's stand-in for <c>FILE*</c>; <see cref="Libc.stdout"/> /
/// <see cref="Libc.stdin"/> are the obvious BCL streams).
/// </summary>
/// <remarks>
/// These operate on one byte at a time, so — like the rest of dotcc's UTF-8-in-
/// <c>char*</c> model — they are exactly correct for ASCII (0..127). A byte
/// &gt; 127 written via <see cref="putchar"/> / <see cref="fputc"/> maps to the
/// same-valued UTF-16 code unit rather than being assembled into a multi-byte
/// UTF-8 sequence (which can't be done one byte at a time); use <c>fputs</c> /
/// <c>printf</c> for non-ASCII text. <c>EOF</c> is <c>-1</c>.
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary><c>fputc(c, stream)</c> — write the byte
    /// <c>(unsigned char)<paramref name="c"/></c> to <paramref name="stream"/>;
    /// return the byte written (as <c>int</c>).</summary>
    public static int fputc(int c, TextWriter stream)
    {
        stream.Write((char)(byte)c);
        return c & 0xFF;
    }

    /// <summary><c>putc(c, stream)</c> — identical to <see cref="fputc"/> (real C
    /// allows <c>putc</c> to be a multiply-evaluating macro; dotcc makes it a
    /// plain call).</summary>
    public static int putc(int c, TextWriter stream) => fputc(c, stream);

    /// <summary><c>putchar(c)</c> ≡ <c>fputc(c, stdout)</c>.</summary>
    public static int putchar(int c) => fputc(c, stdout);

    /// <summary><c>fgetc(stream)</c> — read one byte from
    /// <paramref name="stream"/>; return it (0..255) or <c>EOF</c> (-1) at end of
    /// input.</summary>
    public static int fgetc(TextReader stream)
    {
        int r = stream.Read();
        return r < 0 ? -1 : r;
    }

    /// <summary><c>getc(stream)</c> — identical to <see cref="fgetc"/>.</summary>
    public static int getc(TextReader stream) => fgetc(stream);

    /// <summary><c>getchar()</c> ≡ <c>fgetc(stdin)</c>.</summary>
    public static int getchar() => fgetc(stdin);

    /// <summary>
    /// <c>fgets(s, n, stream)</c> — read at most <c><paramref name="n"/>-1</c>
    /// bytes from <paramref name="stream"/> into <paramref name="s"/>, stopping
    /// after a newline (which is kept) or at EOF, then NUL-terminate. Returns
    /// <paramref name="s"/>, or <c>null</c> if EOF is hit before any byte is read.
    /// </summary>
    public static byte* fgets(byte* s, int n, TextReader stream)
    {
        if (n <= 0) { return null; }
        int i = 0;
        while (i < n - 1)
        {
            int ch = stream.Read();
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

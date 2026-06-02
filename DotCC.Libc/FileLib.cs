#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;stdio.h&gt;</c> file I/O: the <c>FILE*</c> stream surface
/// (<c>fopen</c> / <c>fclose</c> / <c>freopen</c> / <c>fread</c> / <c>fwrite</c> /
/// <c>fseek</c> / <c>ftell</c> / <c>rewind</c> / <c>feof</c> / <c>ferror</c> /
/// <c>clearerr</c> / <c>fflush</c> + <c>remove</c> / <c>rename</c> /
/// <c>tmpfile</c>), plus the three standard streams.
/// </summary>
/// <remarks>
/// <para>
/// <b>FILE model.</b> C code always uses <c>FILE *</c>, never <c>FILE</c> by
/// value, so dotcc keeps <c>FILE</c> a tiny opaque struct (one <c>int</c> slot)
/// and lets <c>FILE*</c> stay a genuine pointer — <c>NULL</c>, <c>==</c>, and
/// <c>if (fp)</c> all work through the normal pointer machinery, no emitter
/// special-casing. The slot indexes <see cref="_files"/>, a managed table that
/// holds the real backing (<see cref="FileSlot"/>).
/// </para>
/// <para>
/// <b>Two backings.</b> Slots 0/1/2 are <c>stdin</c>/<c>stdout</c>/<c>stderr</c>
/// and route text through <see cref="Console.In"/> / <see cref="Console.Out"/> /
/// <see cref="Console.Error"/>, <i>resolved per call</i> so <c>Console.SetIn</c> /
/// <c>SetOut</c> redirection (and the functional harness's stdout capture) is
/// honored — never the raw OS handle. <c>fopen</c>'d slots wrap a
/// <see cref="FileStream"/> (binary, seekable). <c>freopen</c> swaps a slot's
/// backing while keeping the same <c>FILE*</c> identity — the standard way to
/// point <c>stdin</c>/<c>stdout</c> at a file (C99 7.19.5.4).
/// </para>
/// <para>
/// <b>Seekability.</b> <c>fseek</c>/<c>ftell</c> on a console stream return the
/// error result and set <c>errno</c> — terminals/pipes aren't seekable, which
/// is conformant. Only file-backed handles seek.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>
    /// Opaque stdio handle. Holds only a slot index into <see cref="_files"/>,
    /// so a <c>FILE*</c> is a plain pointer-to-struct. Seeded as a known type
    /// name in <c>Compiler.PredefinedTypeNames</c> so user C can spell
    /// <c>FILE *fp</c> with no typedef.
    /// </summary>
    public struct FILE { public int _slot; }

    /// <summary>Managed backing for one open <see cref="FILE"/>.</summary>
    private sealed class FileSlot
    {
        public enum K { In, Out, Err, File }
        public K Kind;
        public Stream? Stream;          // null for the console kinds
        public StreamFileWriter? Writer; // lazy, file kind only
        public StreamFileReader? Reader; // lazy, file kind only
        public bool Eof;
        public bool Err;
        public int Pushback = -1;       // one char pushed back by ungetc (-1 = none)
    }

    // Slots 0/1/2 are the std streams. fopen() appends (or reuses a freed slot).
    private static readonly List<FileSlot?> _files = new()
    {
        new FileSlot { Kind = FileSlot.K.In },
        new FileSlot { Kind = FileSlot.K.Out },
        new FileSlot { Kind = FileSlot.K.Err },
    };
    private static readonly object _filesLock = new();

    // Stable native FILE structs for the std streams, so stdin/stdout/stderr
    // can hand out a fixed FILE* for the program's lifetime.
    private static FILE* _stdinP, _stdoutP, _stderrP;

    private static FILE* AllocStd(int slot)
    {
        var p = (FILE*)NativeMemory.Alloc((nuint)sizeof(FILE));
        p->_slot = slot;
        return p;
    }

    /// <summary>Standard input stream (<c>FILE*</c>). Routes through <see cref="Console.In"/>.</summary>
    public static FILE* stdin => _stdinP != null ? _stdinP : (_stdinP = AllocStd(0));

    /// <summary>Standard output stream (<c>FILE*</c>). Routes through <see cref="Console.Out"/>.</summary>
    public static FILE* stdout => _stdoutP != null ? _stdoutP : (_stdoutP = AllocStd(1));

    /// <summary>Standard error stream (<c>FILE*</c>). Routes through <see cref="Console.Error"/>.</summary>
    public static FILE* stderr => _stderrP != null ? _stderrP : (_stderrP = AllocStd(2));

    private static FileSlot? Slot(FILE* f)
    {
        if (f == null) { return null; }
        int i = f->_slot;
        lock (_filesLock)
        {
            return (uint)i < (uint)_files.Count ? _files[i] : null;
        }
    }

    // ---- text accessors used by fprintf / fscanf (PrintfBuilder/ScanfReader
    //      bind a TextWriter/TextReader; FILE just supplies the right one) ----

    // Pattern-match the slot directly (not `s?.Kind`): the `Stream: { } st`
    // sub-pattern binds the non-null backing stream and `s` binds the matched
    // (non-null) slot, so neither needs a null-forgiving `!`.
    internal static TextWriter WriterFor(FILE* f) => Slot(f) switch
    {
        { Kind: FileSlot.K.Out } => Console.Out,
        { Kind: FileSlot.K.Err } => Console.Error,
        { Kind: FileSlot.K.File, Stream: { } st } s => s.Writer ??= new StreamFileWriter(st),
        _ => TextWriter.Null, // not an output stream — drop, don't throw
    };

    internal static TextReader ReaderFor(FILE* f) => Slot(f) switch
    {
        { Kind: FileSlot.K.In } => Console.In,
        { Kind: FileSlot.K.File, Stream: { } st } s => s.Reader ??= new StreamFileReader(st),
        _ => TextReader.Null,
    };

    // ---- byte helpers used by fputc/fputs/fgetc/fgets ----
    //
    // For files these hit the FileStream byte-exact (fputc/fgetc are
    // byte-oriented in C); for the console they go through Console.* as chars,
    // which is exactly correct for ASCII (matches dotcc's UTF-8-in-char* model).

    internal static void WriteByteTo(FILE* f, byte b)
    {
        var s = Slot(f);
        if (s == null) { return; }
        // Only file-backed slots carry a Stream; the console kinds have none.
        if (s.Stream is { } st) { st.WriteByte(b); }
        else { (s.Kind == FileSlot.K.Err ? Console.Error : Console.Out).Write((char)b); }
    }

    internal static int ReadByteFrom(FILE* f)
    {
        var s = Slot(f);
        if (s == null) { return -1; }
        // A byte pushed back by ungetc is returned before touching the stream
        // (covers fgetc / getc / getchar / fgets — the common ungetc consumers).
        if (s.Pushback >= 0) { int p = s.Pushback; s.Pushback = -1; return p; }
        int r;
        if (s.Stream is { } st)
        {
            r = (s.Reader ??= new StreamFileReader(st)).Read(); // shares the scanf pushback
        }
        else
        {
            r = Console.In.Read();
        }
        if (r < 0) { s.Eof = true; return -1; }
        return r & 0xFF;
    }

    // ---------------------------------------------------------------------
    // fopen / freopen / fclose
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>fopen(path, mode)</c> — open <paramref name="path"/> per the C mode
    /// string (<c>r</c>/<c>w</c>/<c>a</c>, optional <c>+</c> for read-write,
    /// optional <c>b</c> ignored — .NET streams are binary). Returns a
    /// <c>FILE*</c> or <c>null</c> (with <c>errno</c> set) on failure.
    /// </summary>
    public static FILE* fopen(byte* path, byte* mode)
    {
        if (path == null || mode == null) { errno = EINVAL; return null; }
        var p = Encoding.UTF8.GetString(path, strlen(path));
        if (!ParseMode(mode, out var fileMode, out var access)) { errno = EINVAL; return null; }
        Stream stream;
        try
        {
            stream = new FileStream(p, fileMode, access,
                access == FileAccess.Read ? FileShare.Read : FileShare.ReadWrite);
        }
        catch (FileNotFoundException) { errno = ENOENT; return null; }
        catch (DirectoryNotFoundException) { errno = ENOENT; return null; }
        catch (UnauthorizedAccessException) { errno = EACCES; return null; }
        catch (IOException) { errno = EIO; return null; }
        int slot = RegisterFileSlot(stream);
        var fp = (FILE*)NativeMemory.Alloc((nuint)sizeof(FILE));
        fp->_slot = slot;
        return fp;
    }

    /// <summary>
    /// <c>freopen(path, mode, stream)</c> — reassociate the existing
    /// <paramref name="stream"/> handle with <paramref name="path"/>, keeping
    /// the same <c>FILE*</c> (C99 7.19.5.4; the canonical way to redirect
    /// <c>stdin</c>/<c>stdout</c> to a file). Returns <paramref name="stream"/>
    /// on success, or <c>null</c> (closing the stream) on failure.
    /// </summary>
    public static FILE* freopen(byte* path, byte* mode, FILE* stream)
    {
        var slot = Slot(stream);
        if (slot == null || mode == null) { errno = EINVAL; return null; }
        // path == NULL: C says change mode of the existing stream — we have no
        // mode state to change, so just return the stream unchanged.
        if (path == null) { return stream; }
        if (!ParseMode(mode, out var fileMode, out var access)) { errno = EINVAL; return null; }
        var p = Encoding.UTF8.GetString(path, strlen(path));
        Stream newStream;
        try
        {
            newStream = new FileStream(p, fileMode, access,
                access == FileAccess.Read ? FileShare.Read : FileShare.ReadWrite);
        }
        catch (FileNotFoundException) { errno = ENOENT; CloseSlot(slot); return null; }
        catch (DirectoryNotFoundException) { errno = ENOENT; CloseSlot(slot); return null; }
        catch (UnauthorizedAccessException) { errno = EACCES; CloseSlot(slot); return null; }
        catch (IOException) { errno = EIO; CloseSlot(slot); return null; }
        // Drop the old backing, adopt the new file backing in place.
        slot.Writer?.Flush();
        slot.Stream?.Dispose();
        slot.Kind = FileSlot.K.File;
        slot.Stream = newStream;
        slot.Writer = null;
        slot.Reader = null;
        slot.Eof = false;
        slot.Err = false;
        return stream;
    }

    /// <summary>
    /// <c>fclose(stream)</c> — flush and close a <c>fopen</c>'d stream. Returns
    /// 0 on success, <c>EOF</c> on error. Closing a std stream flushes it but
    /// leaves the slot usable.
    /// </summary>
    public static int fclose(FILE* stream)
    {
        var slot = Slot(stream);
        if (slot == null) { return -1; }
        CloseSlot(slot);
        // Free the native FILE struct (but never the cached std-stream structs).
        if (stream != _stdinP && stream != _stdoutP && stream != _stderrP)
        {
            NativeMemory.Free(stream);
        }
        return 0;
    }

    private static void CloseSlot(FileSlot slot)
    {
        if (slot.Kind == FileSlot.K.File)
        {
            slot.Writer?.Flush();
            slot.Stream?.Dispose();
            slot.Stream = null;
            slot.Writer = null;
            slot.Reader = null;
            // Mark the table entry reusable by a later fopen.
            lock (_filesLock)
            {
                int idx = _files.IndexOf(slot);
                if (idx >= 3) { _files[idx] = null; }
            }
        }
        else
        {
            // Std stream: flush, keep usable.
            (slot.Kind == FileSlot.K.Err ? Console.Error : Console.Out).Flush();
        }
    }

    private static int RegisterFileSlot(Stream stream)
    {
        var slot = new FileSlot { Kind = FileSlot.K.File, Stream = stream };
        lock (_filesLock)
        {
            for (int i = 3; i < _files.Count; i++)
            {
                if (_files[i] == null) { _files[i] = slot; return i; }
            }
            _files.Add(slot);
            return _files.Count - 1;
        }
    }

    private static bool ParseMode(byte* mode, out FileMode fileMode, out FileAccess access)
    {
        fileMode = FileMode.Open;
        access = FileAccess.Read;
        byte first = *mode;
        bool plus = false;
        for (byte* m = mode; *m != 0; m++) { if (*m == (byte)'+') { plus = true; } }
        switch (first)
        {
            case (byte)'r':
                fileMode = FileMode.Open;
                access = plus ? FileAccess.ReadWrite : FileAccess.Read;
                return true;
            case (byte)'w':
                fileMode = FileMode.Create;
                access = plus ? FileAccess.ReadWrite : FileAccess.Write;
                return true;
            case (byte)'a':
                fileMode = FileMode.Append;
                access = FileAccess.Write; // .NET forbids Append+Read; a+ degrades to write-at-end
                return true;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------------
    // Positioning + status
    // ---------------------------------------------------------------------

    /// <summary><c>SEEK_SET</c> / <c>SEEK_CUR</c> / <c>SEEK_END</c>.</summary>
    public const int SEEK_SET = 0, SEEK_CUR = 1, SEEK_END = 2;

    /// <summary>
    /// <c>fseek(stream, offset, whence)</c> — reposition a file stream.
    /// Returns 0 on success, nonzero on error (non-seekable console stream
    /// sets <c>errno</c> = ESPIPE). Clears EOF on success, per C.
    /// </summary>
    public static int fseek(FILE* stream, long offset, int whence)
    {
        var slot = Slot(stream);
        if (slot == null || slot.Kind != FileSlot.K.File || slot.Stream is not { CanSeek: true } st)
        {
            errno = ESPIPE;
            return -1;
        }
        var origin = whence switch
        {
            SEEK_CUR => SeekOrigin.Current,
            SEEK_END => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };
        // A pending one-byte read-ahead would desync the position — drop it.
        slot.Reader?.ResetPushback();
        try { st.Seek(offset, origin); }
        catch (IOException) { errno = EIO; return -1; }
        slot.Eof = false;
        return 0;
    }

    /// <summary><c>ftell(stream)</c> — current file position, or -1
    /// (errno ESPIPE) on a non-seekable stream.</summary>
    public static long ftell(FILE* stream)
    {
        var slot = Slot(stream);
        if (slot == null || slot.Kind != FileSlot.K.File || slot.Stream is not { CanSeek: true } st)
        {
            errno = ESPIPE;
            return -1;
        }
        long pos = st.Position;
        // Account for a byte buffered by a pending Peek().
        if (slot.Reader is { HasPushback: true }) { pos -= 1; }
        return pos;
    }

    /// <summary><c>rewind(stream)</c> ≡ <c>fseek(stream, 0, SEEK_SET)</c> and
    /// clears the error indicator.</summary>
    public static void rewind(FILE* stream)
    {
        fseek(stream, 0, SEEK_SET);
        var slot = Slot(stream);
        if (slot != null) { slot.Err = false; }
    }

    /// <summary><c>feof(stream)</c> — nonzero once a read hit end-of-file.</summary>
    public static int feof(FILE* stream) => Slot(stream)?.Eof == true ? 1 : 0;

    /// <summary><c>ferror(stream)</c> — nonzero if an error occurred on the stream.</summary>
    public static int ferror(FILE* stream) => Slot(stream)?.Err == true ? 1 : 0;

    /// <summary><c>clearerr(stream)</c> — clear the EOF and error indicators.</summary>
    public static void clearerr(FILE* stream)
    {
        var slot = Slot(stream);
        if (slot != null) { slot.Eof = false; slot.Err = false; }
    }

    /// <summary>
    /// <c>ungetc(c, stream)</c> — push one byte back so the next read on
    /// <paramref name="stream"/> returns it (Lua's number/look-ahead lexing in
    /// liolib leans on this). C guarantees a single character of pushback, so a
    /// second <c>ungetc</c> with no intervening read fails — as does pushing
    /// <c>EOF</c>. Clears the EOF indicator on success. Returns the pushed byte,
    /// or <c>EOF</c> on failure. The byte is honored by <see cref="ReadByteFrom"/>
    /// (fgetc / getc / getchar / fgets).
    /// </summary>
    public static int ungetc(int c, FILE* stream)
    {
        var slot = Slot(stream);
        if (slot == null || c == -1 || slot.Pushback >= 0) { return -1; }
        slot.Pushback = c & 0xFF;
        slot.Eof = false;
        return c & 0xFF;
    }

    /// <summary>
    /// <c>setvbuf(stream, buf, mode, size)</c> — request a buffering mode
    /// (<c>_IOFBF</c>=0 / <c>_IOLBF</c>=1 / <c>_IONBF</c>=2; see include/stdio.h).
    /// dotcc's streams are managed and the BCL owns buffering, so this is a
    /// validated no-op: returns 0 for a valid stream + recognized mode, nonzero
    /// otherwise. The caller's buffer is never adopted.
    /// </summary>
    public static int setvbuf(FILE* stream, byte* buf, int mode, int size)
    {
        if (Slot(stream) == null || mode < 0 || mode > 2) { return -1; }
        return 0;
    }

    // Shared buffer returned by tmpnam(NULL); kept in sync with L_tmpnam.
    private static byte* _tmpnamBuf;

    /// <summary>
    /// <c>tmpnam(s)</c> — produce a unique temp-file name (NOT created on disk,
    /// per C). Writes into <paramref name="s"/> (which must hold at least
    /// <c>L_tmpnam</c> bytes) when non-NULL, else into a shared internal buffer;
    /// returns that buffer, or <c>NULL</c> if no name fitting <c>L_tmpnam</c>
    /// could be produced. Uses the OS temp directory + a random component.
    /// </summary>
    public static byte* tmpnam(byte* s)
    {
        const int LTmpnam = 260;  // keep in sync with L_tmpnam in include/stdio.h
        var name = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length + 1 > LTmpnam) { return null; }
        byte* dst = s != null ? s
            : _tmpnamBuf != null ? _tmpnamBuf
            : (_tmpnamBuf = (byte*)NativeMemory.Alloc((nuint)LTmpnam));
        for (int i = 0; i < bytes.Length; i++) { dst[i] = bytes[i]; }
        dst[bytes.Length] = 0;
        return dst;
    }

    /// <summary><c>fflush(stream)</c> — flush buffered output. <c>fflush(NULL)</c>
    /// flushes the console streams. Returns 0.</summary>
    public static int fflush(FILE* stream)
    {
        if (stream == null)
        {
            Console.Out.Flush();
            Console.Error.Flush();
            return 0;
        }
        var slot = Slot(stream);
        if (slot?.Kind == FileSlot.K.File) { slot.Writer?.Flush(); slot.Stream?.Flush(); }
        else if (slot?.Kind == FileSlot.K.Err) { Console.Error.Flush(); }
        else { Console.Out.Flush(); }
        return 0;
    }

    // ---------------------------------------------------------------------
    // Binary I/O
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>fread(ptr, size, nmemb, stream)</c> — read up to
    /// <paramref name="nmemb"/> elements of <paramref name="size"/> bytes.
    /// Returns the number of complete elements read (&lt; nmemb at EOF). Sets
    /// the EOF indicator when input runs out.
    /// </summary>
    public static int fread(void* ptr, int size, int nmemb, FILE* stream)
    {
        if (size <= 0 || nmemb <= 0) { return 0; }
        var dst = (byte*)ptr;
        long total = (long)size * nmemb;
        long got = 0;
        for (; got < total; got++)
        {
            int b = ReadByteFrom(stream);
            if (b < 0) { break; }
            dst[got] = (byte)b;
        }
        return (int)(got / size); // complete elements
    }

    /// <summary>
    /// <c>fwrite(ptr, size, nmemb, stream)</c> — write
    /// <paramref name="nmemb"/> elements of <paramref name="size"/> bytes.
    /// Returns the number of complete elements written.
    /// </summary>
    public static int fwrite(void* ptr, int size, int nmemb, FILE* stream)
    {
        if (size <= 0 || nmemb <= 0) { return 0; }
        var src = (byte*)ptr;
        long total = (long)size * nmemb;
        for (long i = 0; i < total; i++) { WriteByteTo(stream, src[i]); }
        return nmemb;
    }

    // ---------------------------------------------------------------------
    // Filesystem helpers
    // ---------------------------------------------------------------------

    /// <summary><c>remove(path)</c> — delete a file. 0 on success, -1 (errno) on failure.</summary>
    public static int remove(byte* path)
    {
        try { File.Delete(Encoding.UTF8.GetString(path, strlen(path))); return 0; }
        catch (FileNotFoundException) { errno = ENOENT; return -1; }
        catch (DirectoryNotFoundException) { errno = ENOENT; return -1; }
        catch (IOException) { errno = EIO; return -1; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
    }

    /// <summary><c>rename(old, new)</c> — rename/move a file. 0 on success, -1 (errno) on failure.</summary>
    public static int rename(byte* oldp, byte* newp)
    {
        try
        {
            File.Move(Encoding.UTF8.GetString(oldp, strlen(oldp)),
                      Encoding.UTF8.GetString(newp, strlen(newp)), overwrite: true);
            return 0;
        }
        catch (FileNotFoundException) { errno = ENOENT; return -1; }
        catch (DirectoryNotFoundException) { errno = ENOENT; return -1; }
        catch (IOException) { errno = EIO; return -1; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
    }

    /// <summary>
    /// <c>tmpfile()</c> — open an anonymous read/write temp file that is
    /// deleted on close. Returns a <c>FILE*</c> or <c>null</c> on failure.
    /// </summary>
    public static FILE* tmpfile()
    {
        Stream stream;
        try
        {
            var path = Path.GetTempFileName();
            stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException) { errno = EIO; return null; }
        int slot = RegisterFileSlot(stream);
        var fp = (FILE*)NativeMemory.Alloc((nuint)sizeof(FILE));
        fp->_slot = slot;
        return fp;
    }

    // ---------------------------------------------------------------------
    // File-backed TextWriter / TextReader adapters
    // ---------------------------------------------------------------------

    /// <summary>
    /// Unbuffered <see cref="TextWriter"/> over a byte <see cref="Stream"/>:
    /// each write encodes UTF-8 straight to the stream, so formatted output
    /// (<c>fprintf</c>) and raw byte output (<c>fputc</c>/<c>fwrite</c>) on the
    /// same handle stay correctly ordered.
    /// </summary>
    internal sealed class StreamFileWriter : TextWriter
    {
        private readonly Stream _s;
        public StreamFileWriter(Stream s) { _s = s; }
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char c)
        {
            if (c < 0x80) { _s.WriteByte((byte)c); return; }
            Span<char> one = stackalloc char[1] { c };
            Span<byte> buf = stackalloc byte[4];
            int n = Encoding.UTF8.GetBytes(one, buf);
            _s.Write(buf[..n]);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            var bytes = Encoding.UTF8.GetBytes(value);
            _s.Write(bytes, 0, bytes.Length);
        }

        public override void Flush() => _s.Flush();
    }

    /// <summary>
    /// Byte-at-a-time <see cref="TextReader"/> over a <see cref="Stream"/> with a
    /// one-byte pushback for <see cref="Peek"/>. Returns each byte as a char
    /// (0..255) — exact for <c>fgetc</c>/<c>fgets</c> (byte-oriented) and for
    /// ASCII <c>fscanf</c>; it deliberately does not decode multi-byte UTF-8
    /// (which would desync the byte position that <c>ftell</c> reports).
    /// </summary>
    internal sealed class StreamFileReader : TextReader
    {
        private readonly Stream _s;
        private int _pushed = -1;
        public StreamFileReader(Stream s) { _s = s; }

        public bool HasPushback => _pushed >= 0;
        public void ResetPushback() => _pushed = -1;

        public override int Peek()
        {
            if (_pushed < 0) { _pushed = _s.ReadByte(); }
            return _pushed;
        }

        public override int Read()
        {
            if (_pushed >= 0) { int r = _pushed; _pushed = -1; return r; }
            return _s.ReadByte();
        }
    }
}

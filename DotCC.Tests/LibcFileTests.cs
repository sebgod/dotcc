#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;stdio.h&gt;</c> <c>FILE*</c> surface (FileLib.cs):
/// fopen/fclose, fprintf/fputs/fputc, fgets/fgetc, fread/fwrite, fseek/ftell/
/// rewind, feof/ferror, remove/rename, tmpfile. Each test uses a real temp
/// file (hermetic, cleaned up) or an anonymous <c>tmpfile()</c>.
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcFileTests
{
    // Marshal a managed string to a NUL-terminated UTF-8 native buffer.
    private static byte* C(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);
    private static void Free(byte* p) => Marshal.FreeCoTaskMem((nint)p);

    private static string Read(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.UTF8.GetString(p, strlen(p));

    [Fact]
    public void fopen_missing_file_for_read_returns_null_and_sets_errno()
    {
        var path = C(Path.Combine(Path.GetTempPath(), "dotcc_definitely_missing_" + Guid.NewGuid().ToString("N") + ".txt"));
        try
        {
            errno = 0;
            FILE* fp = fopen(path, C("r"));
            ((nint)fp).ShouldBe((nint)0);
            errno.ShouldBe(ENOENT);
        }
        finally { Free(path); }
    }

    [Fact]
    public void write_then_read_roundtrip_via_real_file()
    {
        var p = Path.Combine(Path.GetTempPath(), "dotcc_rt_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p);
        try
        {
            FILE* w = fopen(path, C("w"));
            ((nint)w).ShouldNotBe((nint)0);
            fprintf(w, C("n=%d\n")).Arg(7).Done();
            fputs(C("second line\n"), w);
            fputc((byte)'Z', w);
            fclose(w).ShouldBe(0);

            FILE* r = fopen(path, C("r"));
            ((nint)r).ShouldNotBe((nint)0);
            byte* buf = stackalloc byte[64];
            Read(fgets(buf, 64, r)).ShouldBe("n=7\n");
            Read(fgets(buf, 64, r)).ShouldBe("second line\n");
            Read(fgets(buf, 64, r)).ShouldBe("Z");
            // EOF: next fgets returns null and feof flips on.
            ((nint)fgets(buf, 64, r)).ShouldBe((nint)0);
            feof(r).ShouldBe(1);
            fclose(r);
        }
        finally { Free(path); File.Delete(p); }
    }

    [Fact]
    public void fseek_and_ftell_position_within_a_file()
    {
        FILE* fp = tmpfile();
        ((nint)fp).ShouldNotBe((nint)0);
        fputs(C("ABCDEFGHIJ"), fp);    // 10 bytes
        ftell(fp).ShouldBe(10L);

        fseek(fp, 0, SEEK_SET).ShouldBe(0);
        ftell(fp).ShouldBe(0L);
        fgetc(fp).ShouldBe((int)'A');
        ftell(fp).ShouldBe(1L);

        fseek(fp, 4, SEEK_SET);
        fgetc(fp).ShouldBe((int)'E');

        fseek(fp, -1, SEEK_END);
        fgetc(fp).ShouldBe((int)'J');

        rewind(fp);
        ftell(fp).ShouldBe(0L);
        fclose(fp);
    }

    [Fact]
    public void fwrite_then_fread_binary_roundtrip()
    {
        FILE* fp = tmpfile();
        byte* src = stackalloc byte[5] { 1, 2, 254, 0, 255 };
        fwrite(src, 1, 5, fp).ShouldBe(5);

        rewind(fp);
        byte* dst = stackalloc byte[5];
        fread(dst, 1, 5, fp).ShouldBe(5);
        for (int i = 0; i < 5; i++) { dst[i].ShouldBe(src[i]); }

        // Reading past the end returns a short count and sets EOF.
        byte* extra = stackalloc byte[4];
        fread(extra, 1, 4, fp).ShouldBe(0);
        feof(fp).ShouldBe(1);
        fclose(fp);
    }

    [Fact]
    public void fseek_on_a_console_stream_fails_with_espipe()
    {
        errno = 0;
        fseek(stdout, 0, SEEK_SET).ShouldBe(-1);
        errno.ShouldBe(ESPIPE);
        ftell(stdout).ShouldBe(-1L);
    }

    [Fact]
    public void remove_and_rename_operate_on_real_files()
    {
        var a = Path.Combine(Path.GetTempPath(), "dotcc_a_" + Guid.NewGuid().ToString("N") + ".txt");
        var b = Path.Combine(Path.GetTempPath(), "dotcc_b_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(a, "x");
        var pa = C(a); var pb = C(b);
        try
        {
            rename(pa, pb).ShouldBe(0);
            File.Exists(a).ShouldBeFalse();
            File.Exists(b).ShouldBeTrue();
            remove(pb).ShouldBe(0);
            File.Exists(b).ShouldBeFalse();
        }
        finally { Free(pa); Free(pb); if (File.Exists(a)) File.Delete(a); if (File.Exists(b)) File.Delete(b); }
    }

    [Fact]
    public void freopen_redirects_stdout_to_a_file_then_back()
    {
        // Redirecting the *shared* stdout slot is process-global; do it on a
        // fopen'd handle instead to keep the test isolated, exercising the same
        // backing-swap path freopen(…, stdout) uses.
        var p = Path.Combine(Path.GetTempPath(), "dotcc_fr_" + Guid.NewGuid().ToString("N") + ".txt");
        var p2 = Path.Combine(Path.GetTempPath(), "dotcc_fr2_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p); var path2 = C(p2);
        try
        {
            FILE* fp = fopen(path, C("w"));
            fputs(C("first"), fp);
            FILE* same = freopen(path2, C("w"), fp);
            ((nint)same).ShouldBe((nint)fp);   // identity preserved
            fputs(C("second"), same);
            fclose(same);
            File.ReadAllText(p).ShouldBe("first");
            File.ReadAllText(p2).ShouldBe("second");
        }
        finally { Free(path); Free(path2); File.Delete(p); File.Delete(p2); }
    }

    [Fact]
    public void reading_a_write_only_file_returns_EOF_and_sets_ferror_not_a_throw()
    {
        // C's fgetc on a write-only stream returns EOF and sets the error
        // indicator (it must NOT fault) — Lua's file:read relies on this to
        // return (nil, msg, errno). Before the fix the .NET FileStream threw
        // NotSupportedException, crashing the program.
        var p = Path.Combine(Path.GetTempPath(), "dotcc_wo_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p);
        try
        {
            FILE* w = fopen(path, C("w"));
            ((nint)w).ShouldNotBe((nint)0);
            errno = 0;
            fgetc(w).ShouldBe(-1);          // EOF, not a throw
            ferror(w).ShouldNotBe(0);       // error indicator set
            feof(w).ShouldBe(0);            // NOT end-of-file (it's an error)
            fclose(w);
        }
        finally { Free(path); File.Delete(p); }
    }

    [Fact]
    public void writing_a_read_only_file_returns_short_count_and_sets_ferror_not_a_throw()
    {
        // Symmetric: fwrite/fputc on a read-only stream return a short count / EOF
        // and set the error indicator, rather than faulting.
        var p = Path.Combine(Path.GetTempPath(), "dotcc_ro_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p);
        try
        {
            File.WriteAllText(p, "data");
            FILE* r = fopen(path, C("r"));
            ((nint)r).ShouldNotBe((nint)0);
            byte* buf = stackalloc byte[4] { (byte)'a', (byte)'b', (byte)'c', (byte)'d' };
            errno = 0;
            fwrite(buf, 1, 4, r).ShouldBe(0);   // nothing written
            fputc((byte)'x', r).ShouldBe(-1);   // EOF
            ferror(r).ShouldNotBe(0);           // error indicator set
            fclose(r);
        }
        finally { Free(path); File.Delete(p); }
    }

    [Fact]
    public void remove_of_a_missing_file_fails_with_ENOENT()
    {
        // .NET's File.Delete is a silent no-op on a missing file; C's remove()
        // must fail (Lua's os.remove of an already-deleted file returns an error).
        var p = Path.Combine(Path.GetTempPath(), "dotcc_rm_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p);
        try
        {
            File.WriteAllText(p, "x");
            remove(path).ShouldBe(0);       // first removal succeeds
            errno = 0;
            remove(path).ShouldBe(-1);      // second removal fails…
            errno.ShouldBe(ENOENT);         // …with ENOENT
        }
        finally { Free(path); File.Delete(p); }
    }

    [Fact]
    public void same_file_can_be_open_for_write_and_read_concurrently()
    {
        // Unix allows a file to have simultaneous read and write handles; Windows
        // rejects it unless every handle shares permissively. fopen uses
        // FileShare.ReadWrite|Delete so the pattern (Lua's files.lua buffer tests)
        // works on Windows too.
        var p = Path.Combine(Path.GetTempPath(), "dotcc_cc_" + Guid.NewGuid().ToString("N") + ".txt");
        var path = C(p);
        try
        {
            FILE* w = fopen(path, C("w"));
            ((nint)w).ShouldNotBe((nint)0);
            FILE* r = fopen(path, C("r"));      // must NOT fail with EIO
            ((nint)r).ShouldNotBe((nint)0);
            fclose(w); fclose(r);
        }
        finally { Free(path); File.Delete(p); }
    }

    [Fact]
    public void dev_null_discards_writes_and_dev_full_fails_to_flush()
    {
        FILE* devnull = fopen(C("/dev/null"), C("w"));
        ((nint)devnull).ShouldNotBe((nint)0);
        fputc((byte)'x', devnull).ShouldBe((int)'x');   // accepted (discarded)
        fflush(devnull).ShouldBe(0);                    // flush succeeds
        fclose(devnull).ShouldBe(0);

        FILE* devfull = fopen(C("/dev/full"), C("w"));
        ((nint)devfull).ShouldNotBe((nint)0);
        fputc((byte)'x', devfull).ShouldBe((int)'x');   // write accepted…
        fflush(devfull).ShouldBe(-1);                   // …but flush fails (ENOSPC)
        fclose(devfull).ShouldBe(0);
    }
}

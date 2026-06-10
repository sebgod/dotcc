#nullable enable

using System;
using System.IO;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The thin-POSIX surface beyond <c>&lt;unistd.h&gt;</c>: <c>&lt;fcntl.h&gt;</c>,
/// <c>&lt;poll.h&gt;</c>, <c>&lt;sys/stat.h&gt;</c>, <c>&lt;sys/socket.h&gt;</c>,
/// <c>&lt;sys/time.h&gt;</c>. Present so portable Unix C (chibi-scheme's
/// non-<c>_WIN32</c> path) compiles AND behaves honestly at runtime:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>stat</c>/<c>fstat</c> answer existence and the common fields
/// truthfully (File/Directory metadata; open slot).</item>
/// <item><c>gettimeofday</c> is faithful (UTC wall clock, µs truncated from
/// 100 ns ticks).</item>
/// <item><c>fcntl</c> reports no fd flags and accepts (ignores) flag stores —
/// dotcc has no fd-level nonblocking I/O, so an O_NONBLOCK readiness probe
/// degrades to a blocking read (identical behavior for file-backed fds, the
/// only kind DotCC.Libc creates).</item>
/// <item><c>poll</c> claims every polled fd ready — true for file-backed fds.</item>
/// <item><c>shutdown</c> always fails with -1: no fd is ever a socket, and
/// that is exactly what a real libc returns for a non-socket fd.</item>
/// </list>
/// Struct-typed parameters (<c>struct stat*</c>, <c>struct pollfd*</c>,
/// <c>struct timeval*</c>) arrive as <c>void*</c>: the C structs are declared
/// in the synthetic headers and emitted into the program, so the runtime
/// addresses their fields by LAYOUT (documented offsets below), not by type.
/// </remarks>
public static unsafe partial class Libc
{
    // ---- <sys/stat.h> ----------------------------------------------------

    // struct stat layout (LP64, see include/sys/stat.h):
    //   0 st_dev(8) 8 st_ino(8) 16 st_mode(4) 20 pad(4) 24 st_nlink(8)
    //   32 st_uid(4) 36 st_gid(4) 40 st_size(8) 48 st_atime(8)
    //   56 st_mtime(8) 64 st_ctime(8)
    private const int StModeOff = 16, StSizeOff = 40, StAtimeOff = 48, StMtimeOff = 56, StCtimeOff = 64;
    private const uint S_IFDIR = 0x4000, S_IFREG = 0x8000;

    /// <summary><c>stat(path, buf)</c> — 0 when <paramref name="path"/> exists
    /// (file or directory), filling mode/size/times; -1 (ENOENT) otherwise.</summary>
    public static int stat(byte* path, void* buf)
    {
        var name = Encoding.UTF8.GetString(path, strlen(path));
        try
        {
            if (File.Exists(name))
            {
                var fi = new FileInfo(name);
                FillStat(buf, S_IFREG, fi.Length, fi.LastWriteTimeUtc);
                return 0;
            }
            if (Directory.Exists(name))
            {
                FillStat(buf, S_IFDIR, 0, Directory.GetLastWriteTimeUtc(name));
                return 0;
            }
        }
        catch (IOException) { /* fall through to ENOENT */ }
        errno = ENOENT;
        return -1;
    }

    /// <summary><c>fstat(fd, buf)</c> — 0 for an open dotcc fd (mode = regular
    /// file, size from the backing stream where seekable); -1 otherwise.</summary>
    public static int fstat(int fd, void* buf)
    {
        var len = 0L;
        if (fd is 0 or 1 or 2)
        {
            // std streams: character devices in spirit; report a regular file
            // of size 0 (no caller of dotcc's surface distinguishes).
        }
        else
        {
            var slot = SlotByFd(fd);
            if (slot?.Stream == null) { errno = EBADF; return -1; }
            len = slot.Stream.CanSeek ? slot.Stream.Length : 0;
        }
        FillStat(buf, S_IFREG, len, DateTime.UtcNow);
        return 0;
    }

    private static void FillStat(void* buf, uint mode, long size, DateTime mtimeUtc)
    {
        var b = (byte*)buf;
        new Span<byte>(b, 72).Clear();
        *(uint*)(b + StModeOff) = mode;
        *(long*)(b + StSizeOff) = size;
        var unix = new DateTimeOffset(mtimeUtc).ToUnixTimeSeconds();
        *(long*)(b + StAtimeOff) = unix;
        *(long*)(b + StMtimeOff) = unix;
        *(long*)(b + StCtimeOff) = unix;
    }

    // ---- <sys/time.h> ----------------------------------------------------

    /// <summary><c>gettimeofday(tv, tz)</c> — UTC wall clock into
    /// <c>struct timeval { long tv_sec; long tv_usec; }</c>. The obsolete
    /// timezone argument is ignored (NULL on all modern callers). Always 0.</summary>
    public static int gettimeofday(void* tv, void* tz)
    {
        var t = (long*)tv;
        var ticks = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks; // 100 ns units
        t[0] = ticks / TimeSpan.TicksPerSecond;
        t[1] = ticks % TimeSpan.TicksPerSecond / 10;
        return 0;
    }

    // ---- <fcntl.h> ---------------------------------------------------------

    /// <summary><c>fcntl(fd, F_GETFL)</c> — no flags are ever set (0).</summary>
    public static int fcntl(int fd, int cmd) => 0;

    /// <summary><c>fcntl(fd, F_SETFL, flags)</c> — accepted and ignored (see
    /// the class remarks for why this is honest enough). fcntl is variadic in
    /// C, so the flag argument arrives at whatever width the caller's
    /// expression promoted to — hence the overload set.</summary>
    public static int fcntl(int fd, int cmd, int arg) => 0;

    /// <inheritdoc cref="fcntl(int, int, int)"/>
    public static int fcntl(int fd, int cmd, long arg) => 0;

    /// <inheritdoc cref="fcntl(int, int, int)"/>
    public static int fcntl(int fd, int cmd, ulong arg) => 0;

    // ---- <poll.h> ----------------------------------------------------------

    /// <summary><c>poll(fds, nfds, timeout)</c> — every polled fd is reported
    /// ready (file-backed fds always are): each <c>revents</c> echoes
    /// <c>events</c>, return = nfds. struct pollfd layout: fd(4) events(2)
    /// revents(2).</summary>
    public static int poll(void* fds, ulong nfds, int timeout)
    {
        var p = (byte*)fds;
        for (ulong i = 0; i < nfds; i++, p += 8)
        {
            *(short*)(p + 6) = *(short*)(p + 4); // revents = events
        }
        return (int)nfds;
    }

    // ---- <sys/socket.h> ----------------------------------------------------

    /// <summary><c>shutdown(fd, how)</c> — no dotcc fd is a socket; fail with
    /// -1 exactly as a real libc does for a non-socket fd (ENOTSOCK).</summary>
    public static int shutdown(int fd, int how)
    {
        errno = ENOTSOCK;
        return -1;
    }
}

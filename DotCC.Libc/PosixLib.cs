#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The thin-POSIX surface beyond <c>&lt;unistd.h&gt;</c>: <c>&lt;fcntl.h&gt;</c>,
/// <c>&lt;poll.h&gt;</c>, <c>&lt;sys/stat.h&gt;</c>, <c>&lt;sys/time.h&gt;</c>.
/// (<c>&lt;sys/socket.h&gt;</c> is now real — see <c>SocketLib</c>.) Present so
/// portable Unix C (chibi-scheme's non-<c>_WIN32</c> path) compiles AND behaves
/// honestly at runtime:
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

    /// <summary><c>lstat(path, buf)</c> — like <see cref="stat"/> but does not
    /// follow a final symlink. dotcc reports the same metadata either way (the
    /// fields it fills don't distinguish), so this aliases <c>stat</c>.</summary>
    public static int lstat(byte* path, void* buf) => stat(path, buf);

    private static void FillStat(void* buf, uint mode, long size, DateTime mtimeUtc)
    {
        var b = (byte*)buf;
        // struct stat is 96 bytes after the appended st_rdev/st_blksize/st_blocks
        // (see include/sys/stat.h) — clear all of it so those report 0.
        new Span<byte>(b, 96).Clear();
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

    /// <summary><c>settimeofday(tv, tz)</c> — a managed process can't set the
    /// system clock; fail with EPERM (exactly what a non-root process gets).</summary>
    public static int settimeofday(void* tv, void* tz)
    {
        errno = EPERM;
        return -1;
    }

    /// <summary><c>getrusage(who, usage)</c> — per-process resource usage.
    /// Forwards to <c>getrusage(2)</c> on POSIX. On Windows there's no single
    /// rusage call, so we fill the same 144-byte Linux <c>struct rusage</c>
    /// layout (see include/sys/resource.h — the emitted struct is identical on
    /// every host) from GetProcessTimes (ru_utime/ru_stime, the CPU fields that
    /// matter) and, best-effort, GetProcessMemoryInfo (ru_maxrss = peak working
    /// set in KB, matching Linux's units); every other field stays 0.</summary>
    public static int getrusage(int who, void* usage)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (PosixGetrusage(who, usage) == 0) { return 0; }
            errno = Marshal.GetLastPInvokeError();
            return -1;
        }

        // struct rusage as longs: [0]=ru_utime.tv_sec [1]=.tv_usec
        // [2]=ru_stime.tv_sec [3]=.tv_usec [4]=ru_maxrss, rest 0.
        new Span<byte>(usage, 144).Clear();
        long* ru = (long*)usage;
        IntPtr self = GetCurrentProcess();   // pseudo-handle (-1); no close needed
        if (GetProcessTimes(self, out _, out _, out long kernel100ns, out long user100ns))
        {
            (ru[0], ru[1]) = Ticks100nsToTimeval(user100ns);    // ru_utime
            (ru[2], ru[3]) = Ticks100nsToTimeval(kernel100ns);  // ru_stime
        }
        var mem = new PROCESS_MEMORY_COUNTERS { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS) };
        if (GetProcessMemoryInfo(self, ref mem, mem.cb))
        {
            ru[4] = (long)(mem.PeakWorkingSetSize / 1024);      // ru_maxrss in KB (Linux units)
        }
        return 0;
    }

    /// <summary>Split a Windows 100-ns FILETIME tick count into POSIX
    /// <c>timeval</c> (seconds, microseconds).</summary>
    private static (long Sec, long Usec) Ticks100nsToTimeval(long ticks100ns)
    {
        long usec = ticks100ns / 10;            // 100ns -> microseconds
        return (usec / 1_000_000, usec % 1_000_000);
    }

    [DllImport("libc", EntryPoint = "getrusage", SetLastError = true)]
    private static extern int PosixGetrusage(int who, void* usage);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

    // PROCESS_MEMORY_COUNTERS: cb + PageFaultCount are DWORD; the rest SIZE_T.
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS counters, uint size);

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
}

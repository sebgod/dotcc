#nullable enable

using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C <c>&lt;errno.h&gt;</c> surface plus the <c>strerror</c> / <c>perror</c>
/// reporting pair (declared in <c>&lt;string.h&gt;</c> / <c>&lt;stdio.h&gt;</c>
/// respectively, but implemented here next to the error-number table).
/// </summary>
/// <remarks>
/// <para>
/// <c>errno</c> is a thread-local <c>int</c> exposed as a settable static
/// property — emitted user code references the bare name <c>errno</c> which
/// binds to <see cref="errno"/> through <c>using static Libc;</c>, so both
/// <c>errno = 0;</c> and <c>if (errno == ERANGE)</c> work. Real C makes
/// <c>errno</c> a macro expanding to a thread-local lvalue; the property is the
/// idiomatic .NET equivalent (per-thread storage, no shared global).
/// </para>
/// <para>
/// The numeric values match the Linux/glibc <c>asm-generic</c> assignments,
/// consistent with dotcc's LP64 / Linux-leaning model. They are mirrored as
/// numeric <c>#define</c>s in <c>&lt;errno.h&gt;</c> so user code and this
/// switch agree.
/// </para>
/// </remarks>
public static unsafe partial class Libc
{
    [ThreadStatic]
    private static int _errno;

    /// <summary><c>errno</c> — the thread-local error indicator. Settable and
    /// readable; initialised to 0 per thread.</summary>
    public static int errno
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _errno;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _errno = value;
    }

    // Error numbers (Linux/glibc asm-generic values). Mirrored in <errno.h>.
    public const int EPERM = 1;     // Operation not permitted
    public const int ENOENT = 2;    // No such file or directory
    public const int ESRCH = 3;     // No such process
    public const int EINTR = 4;     // Interrupted system call
    public const int EIO = 5;       // Input/output error
    public const int ENXIO = 6;     // No such device or address
    public const int E2BIG = 7;     // Argument list too long
    public const int ENOEXEC = 8;   // Exec format error
    public const int EBADF = 9;     // Bad file descriptor
    public const int ECHILD = 10;   // No child processes
    public const int EAGAIN = 11;   // Resource temporarily unavailable
    public const int ENOMEM = 12;   // Cannot allocate memory
    public const int EACCES = 13;   // Permission denied
    public const int EFAULT = 14;   // Bad address
    public const int EBUSY = 16;    // Device or resource busy
    public const int EEXIST = 17;   // File exists
    public const int EXDEV = 18;    // Invalid cross-device link
    public const int ENODEV = 19;   // No such device
    public const int ENOTDIR = 20;  // Not a directory
    public const int EISDIR = 21;   // Is a directory
    public const int EINVAL = 22;   // Invalid argument
    public const int ENFILE = 23;   // Too many open files in system
    public const int EMFILE = 24;   // Too many open files
    public const int ENOTTY = 25;   // Inappropriate ioctl for device
    public const int EFBIG = 27;    // File too large
    public const int ENOSPC = 28;   // No space left on device
    public const int ESPIPE = 29;   // Illegal seek
    public const int EROFS = 30;    // Read-only file system
    public const int EMLINK = 31;   // Too many links
    public const int EPIPE = 32;    // Broken pipe
    public const int EDOM = 33;     // Numerical argument out of domain   (C std)
    public const int ERANGE = 34;   // Numerical result out of range      (C std)
    public const int EILSEQ = 84;   // Invalid or incomplete multibyte/wide char (C std)
    // ---- socket/network errnos (Linux/glibc asm-generic values; SocketLib) ----
    public const int ENOTSOCK = 88;        // Socket operation on non-socket
    public const int EMSGSIZE = 90;        // Message too long
    public const int EPROTONOSUPPORT = 93; // Protocol not supported
    public const int EOPNOTSUPP = 95;      // Operation not supported on socket
    public const int EAFNOSUPPORT = 97;    // Address family not supported
    public const int EADDRINUSE = 98;      // Address already in use
    public const int EADDRNOTAVAIL = 99;   // Cannot assign requested address
    public const int ENETUNREACH = 101;    // Network is unreachable
    public const int ECONNABORTED = 103;   // Software caused connection abort
    public const int ECONNRESET = 104;     // Connection reset by peer
    public const int ENOBUFS = 105;        // No buffer space available
    public const int EISCONN = 106;        // Transport endpoint is already connected
    public const int ENOTCONN = 107;       // Transport endpoint is not connected
    public const int ETIMEDOUT = 110;      // Connection timed out
    public const int ECONNREFUSED = 111;   // Connection refused
    public const int EHOSTUNREACH = 113;   // No route to host
    public const int EINPROGRESS = 115;    // Operation now in progress

    /// <summary>
    /// <c>strerror(errnum)</c> — map an error number to a human-readable message
    /// pointer. The returned <c>byte*</c> points at pinned RVA literal data
    /// (program-lifetime, never freed) — matching C's "pointer to static, may be
    /// reused" contract without any allocation.
    /// </summary>
    public static byte* strerror(int errnum) => errnum switch
    {
        0       => L("Success\0"u8),
        EPERM   => L("Operation not permitted\0"u8),
        ENOENT  => L("No such file or directory\0"u8),
        ESRCH   => L("No such process\0"u8),
        EINTR   => L("Interrupted system call\0"u8),
        EIO     => L("Input/output error\0"u8),
        ENXIO   => L("No such device or address\0"u8),
        E2BIG   => L("Argument list too long\0"u8),
        ENOEXEC => L("Exec format error\0"u8),
        EBADF   => L("Bad file descriptor\0"u8),
        ECHILD  => L("No child processes\0"u8),
        EAGAIN  => L("Resource temporarily unavailable\0"u8),
        ENOMEM  => L("Cannot allocate memory\0"u8),
        EACCES  => L("Permission denied\0"u8),
        EFAULT  => L("Bad address\0"u8),
        EBUSY   => L("Device or resource busy\0"u8),
        EEXIST  => L("File exists\0"u8),
        EXDEV   => L("Invalid cross-device link\0"u8),
        ENODEV  => L("No such device\0"u8),
        ENOTDIR => L("Not a directory\0"u8),
        EISDIR  => L("Is a directory\0"u8),
        EINVAL  => L("Invalid argument\0"u8),
        ENFILE  => L("Too many open files in system\0"u8),
        EMFILE  => L("Too many open files\0"u8),
        ENOTTY  => L("Inappropriate ioctl for device\0"u8),
        EFBIG   => L("File too large\0"u8),
        ENOSPC  => L("No space left on device\0"u8),
        ESPIPE  => L("Illegal seek\0"u8),
        EROFS   => L("Read-only file system\0"u8),
        EMLINK  => L("Too many links\0"u8),
        EPIPE   => L("Broken pipe\0"u8),
        EDOM    => L("Numerical argument out of domain\0"u8),
        ERANGE  => L("Numerical result out of range\0"u8),
        EILSEQ  => L("Invalid or incomplete multibyte or wide character\0"u8),
        _       => L("Unknown error\0"u8),
    };

    /// <summary>
    /// <c>perror(s)</c> — write <c><paramref name="s"/>: &lt;message&gt;</c>
    /// (then a newline) for the current <see cref="errno"/> to
    /// <see cref="stderr"/>. A null or empty <paramref name="s"/> prints just the
    /// message (matches C, which omits the prefix and separator).
    /// </summary>
    public static void perror(byte* s)
    {
        if (s != null && *s != 0)
        {
            fputs(s, stderr);
            WriterFor(stderr).Write(": ");
        }
        fputs(strerror(errno), stderr);
        WriteByteTo(stderr, (byte)'\n');
    }
}

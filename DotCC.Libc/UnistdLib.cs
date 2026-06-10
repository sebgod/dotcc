#nullable enable

namespace DotCC.Libc;

/// <summary>
/// The minimal POSIX <c>&lt;unistd.h&gt;</c> surface dotcc's synthetic header
/// declares — just enough for portable Unix C (chibi-scheme's non-<c>_WIN32</c>
/// path) to compile and behave honestly.
/// </summary>
/// <remarks>
/// <c>usleep</c> / <c>isatty</c> have faithful BCL lowerings. The
/// <c>select()</c> family compiles (poll-style code is pervasive in portable C)
/// but <c>select</c> itself THROWS at runtime — .NET exposes no fd-level
/// readiness primitive, and a silent "always ready" would spin-loop the caller.
/// Fail loudly at the use site, per dotcc's no-silent-miscompile rule. The
/// <c>FD_*</c> manipulators are no-ops: their only meaning is as input to the
/// <c>select</c> that throws.
/// </remarks>
public static unsafe partial class Libc
{
    /// <summary>POSIX <c>usleep</c> — suspend for at least <paramref name="usec"/>
    /// microseconds. <c>Thread.Sleep</c> has millisecond granularity; sub-ms
    /// requests round up to 1ms (a sleep may always be longer than asked).</summary>
    public static int usleep(uint usec)
    {
        System.Threading.Thread.Sleep(usec == 0 ? 0 : (int)System.Math.Max(1, usec / 1000));
        return 0;
    }

    /// <summary>POSIX <c>isatty</c> — 1 when the standard stream for
    /// <paramref name="fd"/> (0/1/2) is attached to a console, 0 otherwise
    /// (including unknown fds; dotcc has no other fd table).</summary>
    public static int isatty(int fd) => fd switch
    {
        0 => System.Console.IsInputRedirected ? 0 : 1,
        1 => System.Console.IsOutputRedirected ? 0 : 1,
        2 => System.Console.IsErrorRedirected ? 0 : 1,
        _ => 0,
    };

    /// <summary>POSIX <c>FD_ZERO</c> — no-op; see class remarks.</summary>
    public static void FD_ZERO(long* set) { }

    /// <summary>POSIX <c>FD_SET</c> — no-op; see class remarks.</summary>
    public static void FD_SET(int fd, long* set) { }

    /// <summary>POSIX <c>FD_CLR</c> — no-op; see class remarks.</summary>
    public static void FD_CLR(int fd, long* set) { }

    /// <summary>POSIX <c>FD_ISSET</c> — always 0; see class remarks.</summary>
    public static int FD_ISSET(int fd, long* set) => 0;

    /// <summary>POSIX <c>select</c> — unsupported on .NET; throws so a caller
    /// fails loudly at the use site instead of spin-looping on a fake "ready".</summary>
    public static int select(int nfds, long* readfds, long* writefds, long* errorfds, void* timeout)
        => throw new System.NotSupportedException("select() is not supported by the dotcc runtime");
}

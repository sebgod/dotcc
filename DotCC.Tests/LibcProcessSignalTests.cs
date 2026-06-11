#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the POSIX process/signal surface (<see cref="DotCC.Libc"/>'s
/// ProcessSignalLib): process identity (<c>getpid</c>/<c>getppid</c>) and signals
/// (<c>kill</c>). The pid primitives are the foundation <c>kill(getpid(), 0)</c>
/// stands on, so they're proven against the raw OS syscall here BEFORE kill is
/// exercised.
/// </summary>
public sealed unsafe class LibcProcessSignalTests
{
    // Raw OS process-id calls, used only to cross-check Libc.getpid() against an
    // INDEPENDENT source (comparing to Environment.ProcessId would be circular —
    // that's what getpid() returns).
    [DllImport("libc", EntryPoint = "getpid")]
    private static extern int OsGetpidPosix();

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")]
    private static extern uint OsGetpidWin();

    private static int RealOsPid() =>
        OperatingSystem.IsWindows() ? (int)OsGetpidWin() : OsGetpidPosix();

    [Fact]
    public void getpid_returns_the_real_os_process_id()
    {
        int pid = getpid();
        pid.ShouldBeGreaterThan(0);
        pid.ShouldBe(RealOsPid());                       // the raw OS syscall agrees
        pid.ShouldBe(Process.GetCurrentProcess().Id);    // and the BCL agrees
    }

    [Fact]
    public void getpid_is_stable_across_calls()
    {
        getpid().ShouldBe(getpid());
    }

    [Fact]
    public void getppid_returns_a_plausible_parent_pid()
    {
        // The test host is always spawned by something (the runner / a shell), so
        // a real parent pid exists on both OSes — getppid(2) on Linux, the
        // toolhelp snapshot walk on Windows.
        int ppid = getppid();
        ppid.ShouldBeGreaterThan(0);
        ppid.ShouldNotBe(getpid());     // the parent isn't us
    }

    // ru_utime as a single microsecond count: tv_sec*1e6 + tv_usec. ru_utime is
    // the first field of struct rusage, so [0]=tv_sec, [1]=tv_usec as longs.
    private static long UserMicros(byte* ru)
    {
        long* p = (long*)ru;
        return p[0] * 1_000_000 + p[1];
    }

    // ---- kill ---------------------------------------------------------------
    // These exercise ONLY the sig==0 existence/permission probe — no signal is
    // ever delivered, so there's nothing to clean up and no process is harmed.
    // kill(getpid(), 0) is the one form chibi-style code actually uses.

    [Fact]
    public void kill_self_with_signal_zero_succeeds()
    {
        // sig 0 sends nothing; it only asks "does this pid exist and may I signal
        // it?". Our own pid always answers yes on both OSes (POSIX kill(2) / the
        // Windows OpenProcess probe), so the call returns 0.
        kill(getpid(), 0).ShouldBe(0);
    }

    [Fact]
    public void kill_nonexistent_pid_with_signal_zero_reports_no_such_process()
    {
        // A pid above any possible OS maximum (Linux pid_max tops out well below
        // 2^31; an odd value like int.MaxValue is never a valid Windows pid
        // either). The existence probe therefore fails with ESRCH on both OSes.
        errno = 0;
        kill(int.MaxValue, 0).ShouldBe(-1);
        errno.ShouldBe(ESRCH);
    }

    [Fact]
    public void getrusage_reports_nonzero_monotonic_user_cpu_time()
    {
        const int RUSAGE_SELF = 0;
        byte* ru1 = stackalloc byte[144];
        byte* ru2 = stackalloc byte[144];

        getrusage(RUSAGE_SELF, ru1).ShouldBe(0);
        long u1 = UserMicros(ru1);

        // Burn measurable user CPU between the two samples.
        long acc = 0;
        for (int i = 0; i < 50_000_000; i++) { acc += i; }
        GC.KeepAlive(acc);

        getrusage(RUSAGE_SELF, ru2).ShouldBe(0);
        long u2 = UserMicros(ru2);

        // The host has run for a while, so user CPU time is already > 0 — that it
        // lands in ru_utime (offset 0) and not some other field is the layout
        // proof; getrusage(2) on Linux / GetProcessTimes on Windows. CPU time
        // never decreases, so u2 >= u1.
        u1.ShouldBeGreaterThan(0);
        u2.ShouldBeGreaterThanOrEqualTo(u1);
    }
}

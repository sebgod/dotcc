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
}

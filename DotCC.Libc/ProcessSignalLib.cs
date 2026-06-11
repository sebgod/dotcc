#nullable enable

using System;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// The POSIX process-control and signal surface (<c>&lt;unistd.h&gt;</c>
/// fork/exec, <c>&lt;sys/wait.h&gt;</c>, <c>&lt;signal.h&gt;</c>) chibi-scheme's
/// <c>(chibi process)</c> binds to. .NET has no <c>fork</c> and the runtime owns
/// in-process signal *handling*, so fork/exec/wait stay honest stubs that compile
/// + let the module LOAD. The process *identity* and *targeting* primitives are
/// faithful, though: <c>getpid</c>/<c>getppid</c> report the real OS pids, the
/// <c>sigset_t</c> manipulators are real bitset ops, and <c>kill</c> forwards to
/// the OS (POSIX <c>kill(2)</c> / Windows OpenProcess+TerminateProcess) so the
/// portable <c>kill(pid, 0)</c> existence probe and a SIGKILL/SIGTERM are real.
/// The R7RS suite uses
/// command-line / exit / environment access, not fork/exec — so the
/// stubs are never exercised by it (and fail loudly, EPERM/-1, if a program
/// does call them).
/// </summary>
public static unsafe partial class Libc
{
    // ---- <unistd.h> process control ---------------------------------------

    /// <summary><c>getpid()</c> — the current process id (faithful).</summary>
    public static int getpid() => Environment.ProcessId;

    /// <summary><c>getppid()</c> — parent process id. POSIX <c>getppid(2)</c> is
    /// exact. Windows has no direct API, so we scan the toolhelp process snapshot
    /// for our own entry and read its parent field. Returns 0 if the parent can't
    /// be determined (snapshot failed, or our entry isn't listed).</summary>
    public static int getppid()
    {
        if (OperatingSystem.IsWindows()) { return WinGetParentPid(); }
        return PosixGetppid();
    }

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int PosixGetppid();

    // ---- Windows parent-pid via the toolhelp process snapshot --------------
    // A blittable PROCESSENTRY32W (primitives + a fixed WCHAR buffer) and the
    // toolhelp APIs: no marshalling stub, so it stays AOT-clean and survives the
    // runtime-block splice (same reasoning as link()'s [DllImport]).

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;    // ULONG_PTR
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];  // MAX_PATH
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    /// <summary>Parent pid via the toolhelp snapshot: find our own pid's entry
    /// and return its <c>th32ParentProcessID</c>. 0 if unavailable.</summary>
    private static int WinGetParentPid()
    {
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        uint self = GetCurrentProcessId();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) { return 0; }
        try
        {
            var pe = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (Process32FirstW(snap, ref pe) == 0) { return 0; }
            do
            {
                if (pe.th32ProcessID == self) { return (int)pe.th32ParentProcessID; }
            }
            while (Process32NextW(snap, ref pe) != 0);
            return 0;
        }
        finally { CloseHandle(snap); }
    }

    /// <summary><c>sleep(seconds)</c> — block the calling thread; returns 0 (no
    /// signal interrupts it on .NET, so the full interval always elapses).</summary>
    public static uint sleep(uint seconds)
    {
        System.Threading.Thread.Sleep((int)Math.Min(seconds, int.MaxValue / 1000) * 1000);
        return 0;
    }

    /// <summary><c>fork()</c> — no .NET primitive; fails with EPERM (return -1).
    /// A managed runtime can't duplicate its own address space.</summary>
    public static int fork() { errno = EPERM; return -1; }

    /// <summary><c>execvp(file, argv)</c> — no .NET process-image replacement;
    /// fails with EPERM. (On success exec never returns; -1 is the failure path.)</summary>
    public static int execvp(byte* file, byte** argv) { errno = EPERM; return -1; }

    /// <inheritdoc cref="execvp(byte*, byte**)"/>
    public static int execv(byte* path, byte** argv) { errno = EPERM; return -1; }

    /// <summary><c>alarm(seconds)</c> — no SIGALRM delivery on .NET; accept and
    /// report no previously-scheduled alarm (0).</summary>
    public static uint alarm(uint seconds) => 0;

    /// <summary><c>_exit(status)</c> — immediate termination, no handlers/flush
    /// (POSIX). Same backing as <see cref="_Exit"/>.</summary>
    public static void _exit(int status) => Environment.Exit(status);

    // ---- <sys/wait.h> ------------------------------------------------------

    /// <summary><c>wait(status)</c> — dotcc has no children to reap; -1
    /// (ECHILD in spirit; dotcc carries EPERM as the closest "can't").</summary>
    public static int wait(int* status) { errno = EPERM; return -1; }

    /// <inheritdoc cref="wait(int*)"/>
    public static int waitpid(int pid, int* status, int options) { errno = EPERM; return -1; }

    // ---- <signal.h> --------------------------------------------------------

    /// <summary><c>kill(pid, sig)</c> — send signal <paramref name="sig"/> to
    /// process <paramref name="pid"/>. POSIX <c>kill(2)</c> is exact: the
    /// <c>sig==0</c> existence/permission probe, real delivery, and the
    /// ESRCH/EPERM/EINVAL <c>errno</c> are all the kernel's. Windows has no
    /// signals, so we model the two portable idioms — <c>sig==0</c> becomes an
    /// <c>OpenProcess</c> existence probe, and the terminating signals (SIGKILL 9
    /// / SIGTERM 15 / SIGINT 2) route through <c>TerminateProcess</c>. Any other
    /// signal has no Windows delivery mechanism (EINVAL), as does a
    /// <paramref name="pid"/> &lt;= 0 (POSIX process-group / broadcast target,
    /// unmodeled here).</summary>
    public static int kill(int pid, int sig)
    {
        if (OperatingSystem.IsWindows()) { return WinKill(pid, sig); }
        int rc = PosixKill(pid, sig);
        // Surface the raw OS errno (our constants are the glibc asm-generic values).
        if (rc != 0) { errno = Marshal.GetLastPInvokeError(); }
        return rc;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int PosixKill(int pid, int sig);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, int bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>Windows <c>kill</c>: no signals, so <c>sig 0</c> is an OpenProcess
    /// existence/permission probe and the terminating signals route through
    /// TerminateProcess. <c>errno</c> mirrors POSIX <c>kill(2)</c>: ESRCH for a
    /// missing process, EPERM when it exists but the open is access-denied.</summary>
    private static int WinKill(int pid, int sig)
    {
        const uint PROCESS_TERMINATE = 0x0001;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int ERROR_ACCESS_DENIED = 5;

        // POSIX pid <= 0 targets process groups / every process — not modeled here.
        if (pid <= 0) { errno = EINVAL; return -1; }

        if (sig == 0)
        {
            // Existence/permission probe only — no signal is sent.
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, (uint)pid);
            if (h != IntPtr.Zero) { CloseHandle(h); return 0; }
            errno = Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? EPERM : ESRCH;
            return -1;
        }

        // SIGKILL (9) / SIGTERM (15) / SIGINT (2): the deliverable "terminate" signals.
        if (sig == 9 || sig == 15 || sig == 2)
        {
            IntPtr h = OpenProcess(PROCESS_TERMINATE, 0, (uint)pid);
            if (h == IntPtr.Zero)
            {
                errno = Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED ? EPERM : ESRCH;
                return -1;
            }
            try
            {
                // 128 + signo is the conventional "killed by signal" exit code.
                if (TerminateProcess(h, (uint)(128 + sig)) != 0) { return 0; }
                errno = EPERM;
                return -1;
            }
            finally { CloseHandle(h); }
        }

        // Any other signal has no Windows delivery mechanism.
        errno = EINVAL;
        return -1;
    }

    /// <summary><c>raise(sig)</c> — no self-signal delivery on .NET; -1.</summary>
    public static int raise(int sig) { errno = EPERM; return -1; }

    /// <summary><c>sigaction(sig, act, oldact)</c> — accepted and ignored: the
    /// handler can't fire (the .NET runtime owns signals). Returns 0 so module
    /// init that installs handlers proceeds. <paramref name="act"/>/<paramref
    /// name="oldact"/> are <c>struct sigaction*</c> (emitted struct; addressed as
    /// <c>void*</c> here, like <c>stat</c>).</summary>
    public static int sigaction(int sig, void* act, void* oldact) => 0;

    /// <summary><c>sigprocmask(how, set, oldset)</c> — no real signal mask;
    /// accepted and ignored (0).</summary>
    public static int sigprocmask(int how, ulong* set, ulong* oldset) => 0;

    // sigset_t is `unsigned long` (see <signal.h>): a 64-bit bitset, bit (n-1)
    // for signal n. These manipulators ARE faithful — pure bit twiddling.

    /// <summary><c>sigemptyset(set)</c> — clear all signals. Always 0.</summary>
    public static int sigemptyset(ulong* set) { if (set != null) { *set = 0; } return 0; }

    /// <summary><c>sigfillset(set)</c> — include all signals. Always 0.</summary>
    public static int sigfillset(ulong* set) { if (set != null) { *set = ~0UL; } return 0; }

    /// <summary><c>sigaddset(set, signum)</c> — add signal <paramref name="signum"/>.</summary>
    public static int sigaddset(ulong* set, int signum)
    {
        if (set == null || signum < 1 || signum > 64) { errno = EINVAL; return -1; }
        *set |= 1UL << (signum - 1);
        return 0;
    }

    /// <summary><c>sigdelset(set, signum)</c> — remove signal <paramref name="signum"/>.</summary>
    public static int sigdelset(ulong* set, int signum)
    {
        if (set == null || signum < 1 || signum > 64) { errno = EINVAL; return -1; }
        *set &= ~(1UL << (signum - 1));
        return 0;
    }

    /// <summary><c>sigismember(set, signum)</c> — 1 if present, 0 if not, -1 on
    /// a bad signal number.</summary>
    public static int sigismember(ulong* set, int signum)
    {
        if (set == null || signum < 1 || signum > 64) { errno = EINVAL; return -1; }
        return (int)((*set >> (signum - 1)) & 1);
    }
}

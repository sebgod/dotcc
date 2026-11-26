#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// <c>&lt;sys/mman.h&gt;</c>: <c>mmap</c>, <c>munmap</c>, <c>mprotect</c>, <c>madvise</c> and
/// <c>msync</c>. The header's constants are Linux's. On Linux every call forwards to the host's
/// own as is; on macOS the flags that differ are translated first; on Windows an anonymous
/// mapping is made of <c>VirtualAlloc</c> pages (see the header for what that means for
/// overcommit). Every import is blittable, so <c>[DllImport]</c> needs no marshalling stub
/// under NativeAOT.
/// </summary>
public static unsafe partial class Libc
{
    private const int PROT_NONE = 0, PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
    private const int MAP_FIXED = 0x10, MAP_ANONYMOUS = 0x20, MAP_NORESERVE = 0x4000;
    private const int MADV_DONTNEED = 4;

    /// <summary>The <c>MAP_FAILED</c> result, <c>(void *)-1</c>.</summary>
    private static void* MapFailed => (void*)(nint)(-1);

    /// <summary><c>mmap(addr, length, prot, flags, fd, offset)</c>: a new mapping, or
    /// <c>MAP_FAILED</c> with <c>errno</c> set.</summary>
    public static void* mmap(void* addr, ulong length, int prot, int flags, int fd, long offset)
    {
        if (OperatingSystem.IsWindows()) { return WinMmap(addr, length, prot, flags, fd); }
        var p = PosixMmap(addr, (nuint)length, prot, HostMapFlags(flags), fd, offset);
        if (p == MapFailed) { errno = Marshal.GetLastPInvokeError(); }
        return p;
    }

    /// <summary><c>munmap(addr, length)</c>: 0, or -1 with <c>errno</c> set.</summary>
    public static int munmap(void* addr, ulong length)
    {
        if (OperatingSystem.IsWindows()) { return WinMunmap(addr, length); }
        return PosixResult(PosixMunmap(addr, (nuint)length));
    }

    /// <summary><c>mprotect(addr, length, prot)</c>: 0, or -1 with <c>errno</c> set.</summary>
    public static int mprotect(void* addr, ulong length, int prot)
    {
        if (OperatingSystem.IsWindows()) { return WinMprotect(addr, length, prot); }
        return PosixResult(PosixMprotect(addr, (nuint)length, prot));
    }

    /// <summary><c>madvise(addr, length, advice)</c>: advice the host has no counterpart for is
    /// accepted and does nothing (<c>MADV_NOHUGEPAGE</c> off Linux), as advice may.</summary>
    public static int madvise(void* addr, ulong length, int advice)
    {
        if (OperatingSystem.IsWindows()) { return advice == MADV_DONTNEED ? WinDiscard(addr, length) : 0; }
        if (OperatingSystem.IsLinux()) { return PosixResult(PosixMadvise(addr, (nuint)length, advice)); }
        // macOS and the BSDs share Linux's numbers for advice 0 to 4 and have none past them.
        return advice <= MADV_DONTNEED ? PosixResult(PosixMadvise(addr, (nuint)length, advice)) : 0;
    }

    /// <summary><c>msync(addr, length, flags)</c>: an anonymous mapping has no file to write
    /// back to, so on Windows there is nothing to do.</summary>
    public static int msync(void* addr, ulong length, int flags)
    {
        if (OperatingSystem.IsWindows()) { return 0; }
        return PosixResult(PosixMsync(addr, (nuint)length, flags));
    }

    /// <summary>A POSIX call's 0 / -1 result, with <c>errno</c> taken from the host on failure.</summary>
    private static int PosixResult(int rc)
    {
        if (rc != 0) { errno = Marshal.GetLastPInvokeError(); }
        return rc;
    }

    /// <summary>The header's (Linux's) <c>MAP_*</c> flags as the host spells them: Linux's own,
    /// macOS's <c>MAP_ANON</c> 0x1000 and <c>MAP_NORESERVE</c> 0x40 elsewhere.</summary>
    private static int HostMapFlags(int flags)
    {
        if (OperatingSystem.IsLinux()) { return flags; }
        var host = flags & ~(MAP_ANONYMOUS | MAP_NORESERVE);
        if ((flags & MAP_ANONYMOUS) != 0) { host |= 0x1000; }
        if ((flags & MAP_NORESERVE) != 0) { host |= 0x40; }
        return host;
    }

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern void* PosixMmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int PosixMunmap(void* addr, nuint length);

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int PosixMprotect(void* addr, nuint length, int prot);

    [DllImport("libc", EntryPoint = "madvise", SetLastError = true)]
    private static extern int PosixMadvise(void* addr, nuint length, int advice);

    [DllImport("libc", EntryPoint = "msync", SetLastError = true)]
    private static extern int PosixMsync(void* addr, nuint length, int flags);

    // ---- Windows: anonymous mappings over VirtualAlloc ------------------------

    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_DECOMMIT = 0x4000, MEM_RELEASE = 0x8000;
    private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04,
        PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40;

    /// <summary>The base and length of each mapping <see cref="WinMmap"/> made, so
    /// <see cref="WinMunmap"/> can tell a whole mapping (released) from part of one
    /// (decommitted). Keyed by base address.</summary>
    private static readonly Dictionary<nint, nuint> _winMappings = new();

    /// <summary>The Windows page protection for a POSIX <c>PROT_*</c> set; write implies read,
    /// as it does on x86 and arm64 hosts.</summary>
    private static uint WinProtect(int prot) => (prot & (PROT_READ | PROT_WRITE | PROT_EXEC)) switch
    {
        PROT_NONE => PAGE_NOACCESS,
        PROT_READ => PAGE_READONLY,
        _ when (prot & PROT_EXEC) != 0 => (prot & PROT_WRITE) != 0 ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ,
        _ => (prot & PROT_WRITE) != 0 ? PAGE_READWRITE : PAGE_READONLY,
    };

    private static void* WinMmap(void* addr, ulong length, int prot, int flags, int fd)
    {
        if ((flags & MAP_ANONYMOUS) == 0 || fd != -1) { errno = ENODEV; return MapFailed; }
        if (length == 0) { errno = EINVAL; return MapFailed; }
        var len = (nuint)length;
        if ((flags & MAP_FIXED) != 0)
        {
            // Replace the pages of a mapping already made with fresh zero ones, in place.
            if (WinMappingOf(addr, len) is null) { errno = EINVAL; return MapFailed; }
            VirtualFree(addr, len, MEM_DECOMMIT);
            if (prot != PROT_NONE && VirtualAlloc(addr, len, MEM_COMMIT, WinProtect(prot)) == null)
            {
                errno = ENOMEM;
                return MapFailed;
            }
            return addr;
        }
        // An accessible mapping is committed now: Windows cannot commit a page as it is touched
        // without a fault handler, which .NET does not give a program.
        var type = prot == PROT_NONE ? MEM_RESERVE : MEM_RESERVE | MEM_COMMIT;
        var p = VirtualAlloc(null, len, type, WinProtect(prot));
        if (p == null) { errno = ENOMEM; return MapFailed; }
        lock (_winMappings) { _winMappings[(nint)p] = len; }
        return p;
    }

    /// <summary>The mapping [<paramref name="addr"/>, +<paramref name="len"/>) lies in, as its
    /// base and length, or null when it lies in none.</summary>
    private static (nint Base, nuint Length)? WinMappingOf(void* addr, nuint len)
    {
        var a = (nint)addr;
        lock (_winMappings)
        {
            foreach (var (b, l) in _winMappings)
            {
                if (a >= b && (nuint)(a - b) + len <= l) { return (b, l); }
            }
        }
        return null;
    }

    private static int WinMunmap(void* addr, ulong length)
    {
        var len = (nuint)length;
        if (WinMappingOf(addr, len) is not { } m) { errno = EINVAL; return -1; }
        if (m.Base == (nint)addr && m.Length == len)
        {
            lock (_winMappings) { _winMappings.Remove(m.Base); }
            return VirtualFree(addr, 0, MEM_RELEASE) ? 0 : WinFail(EINVAL);
        }
        // Part of a mapping: its pages go, its address range stays reserved until the rest goes.
        return VirtualFree(addr, len, MEM_DECOMMIT) ? 0 : WinFail(EINVAL);
    }

    private static int WinMprotect(void* addr, ulong length, int prot)
    {
        var len = (nuint)length;
        if (WinMappingOf(addr, len) is null) { errno = ENOMEM; return -1; }
        if (prot != PROT_NONE)
        {
            // Committing is idempotent and sets the protection of pages already committed too.
            return VirtualAlloc(addr, len, MEM_COMMIT, WinProtect(prot)) != null ? 0 : WinFail(ENOMEM);
        }
        // PROT_NONE keeps the pages' contents for a later mprotect. VirtualProtect fails over a
        // range that is not committed throughout, so it walks the committed runs; a reserved run
        // is inaccessible already.
        var at = (byte*)addr;
        var end = at + len;
        while (at < end)
        {
            MEMORY_BASIC_INFORMATION info;
            if (VirtualQuery(at, &info, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) { return WinFail(ENOMEM); }
            var runEnd = Math.Min((ulong)info.BaseAddress + (ulong)info.RegionSize, (ulong)end);
            if (info.State == MEM_COMMIT && !VirtualProtect(at, (nuint)(runEnd - (ulong)at), PAGE_NOACCESS, out _))
            {
                return WinFail(ENOMEM);
            }
            at = (byte*)runEnd;
        }
        return 0;
    }

    /// <summary><c>MADV_DONTNEED</c> on Windows: the committed pages of the range are given back
    /// and committed afresh with their protection, so they read as zeros, as a private anonymous
    /// mapping's do on Linux after it.</summary>
    private static int WinDiscard(void* addr, ulong length)
    {
        var len = (nuint)length;
        if (WinMappingOf(addr, len) is null) { errno = ENOMEM; return -1; }
        var at = (byte*)addr;
        var end = at + len;
        while (at < end)
        {
            MEMORY_BASIC_INFORMATION info;
            if (VirtualQuery(at, &info, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) { return WinFail(ENOMEM); }
            var runEnd = Math.Min((ulong)info.BaseAddress + (ulong)info.RegionSize, (ulong)end);
            var runLen = (nuint)(runEnd - (ulong)at);
            if (info.State == MEM_COMMIT)
            {
                if (!VirtualFree(at, runLen, MEM_DECOMMIT) || VirtualAlloc(at, runLen, MEM_COMMIT, info.Protect) == null)
                {
                    return WinFail(ENOMEM);
                }
            }
            at = (byte*)runEnd;
        }
        return 0;
    }

    /// <summary>Set <c>errno</c> to <paramref name="code"/> and give the failing result, -1.</summary>
    private static int WinFail(int code)
    {
        errno = code;
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public void* BaseAddress;
        public void* AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(void* address, MEMORY_BASIC_INFORMATION* buffer, nuint length);
}

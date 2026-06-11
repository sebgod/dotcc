#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// The POSIX filesystem surface beyond <c>&lt;stdio.h&gt;</c> streams:
/// <c>&lt;dirent.h&gt;</c> directory iteration, <c>open</c>, and the
/// <c>&lt;unistd.h&gt;</c>/<c>&lt;sys/stat.h&gt;</c> path operations
/// (<c>mkdir</c>/<c>rmdir</c>/<c>unlink</c>/<c>chdir</c>/<c>getcwd</c>/…). Present
/// so portable Unix C (chibi-scheme's <c>(chibi filesystem)</c>) compiles AND
/// behaves over <see cref="System.IO"/>.
/// </summary>
/// <remarks>
/// Mappings are faithful where .NET has the primitive (directory enumeration,
/// create/delete, cwd, symlinks). Where .NET has no managed API but the OS does,
/// the call forwards to the real primitive behind an <see cref="OperatingSystem"/>
/// switch — <c>link</c> (hard links) routes to <c>link(2)</c> on POSIX and
/// <c>CreateHardLinkW</c> on Windows. These P/Invokes are blittable
/// (pointers + <c>int</c>), so <c>[DllImport]</c> emits a direct AOT-clean call
/// with no marshalling stub, and — unlike <c>[LibraryImport]</c> — needs no
/// source generator, so it survives the runtime-block splice into the functional
/// tests' generator-less Roslyn compilation. <c>flock</c> (advisory locks) is a
/// success no-op — a single managed process never self-contends, and portable
/// code already tolerates the absence of a cross-process guarantee. <c>chmod</c>
/// is best-effort (Unix mode bits via
/// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> where the host
/// supports it, else a no-op success).
/// </remarks>
public static unsafe partial class Libc
{
    // ---- <dirent.h> --------------------------------------------------------

    /// <summary>Per-open-directory state behind a <c>DIR*</c> token. The token
    /// is a tiny native allocation whose address keys this table; the dirent
    /// buffer is reused across <c>readdir</c> calls (C's "may be overwritten by
    /// the next call" contract).</summary>
    private sealed class DirState
    {
        public required string[] Names;
        public int Pos;
        public byte* Dirent;
    }

    private static readonly Dictionary<nint, DirState> _dirs = new();
    private static readonly object _dirsLock = new();

    // struct dirent (see include/dirent.h): d_name is FIRST, offset 0, 256 bytes.
    private const int DName = 256;

    /// <summary><c>opendir(name)</c> — snapshot the directory's entries (".",
    /// ".." then the real names, like POSIX) and return a <c>DIR*</c> token, or
    /// <c>null</c> (ENOENT/ENOTDIR) if it can't be read.</summary>
    public static void* opendir(byte* name)
    {
        if (name == null) { errno = ENOENT; return null; }
        var path = Encoding.UTF8.GetString(name, strlen(name));
        string[] names;
        try
        {
            if (!Directory.Exists(path)) { errno = ENOENT; return null; }
            var entries = Directory.GetFileSystemEntries(path);
            names = new string[entries.Length + 2];
            names[0] = ".";
            names[1] = "..";
            for (var i = 0; i < entries.Length; i++) { names[i + 2] = Path.GetFileName(entries[i]); }
        }
        catch (UnauthorizedAccessException) { errno = EACCES; return null; }
        catch (IOException) { errno = EIO; return null; }
        var token = (byte*)NativeMemory.Alloc(8);
        var buf = (byte*)NativeMemory.Alloc((nuint)DName + 16);
        lock (_dirsLock) { _dirs[(nint)token] = new DirState { Names = names, Dirent = buf }; }
        return token;
    }

    /// <summary><c>readdir(dirp)</c> — fill and return the reused
    /// <c>struct dirent</c> for the next entry (its <c>d_name</c> at offset 0),
    /// or <c>null</c> at end-of-directory / for an unknown token.</summary>
    public static void* readdir(void* dirp)
    {
        DirState? st;
        lock (_dirsLock) { _dirs.TryGetValue((nint)dirp, out st); }
        if (st == null || st.Pos >= st.Names.Length) { return null; }
        var nm = st.Names[st.Pos++];
        new Span<byte>(st.Dirent, DName).Clear();
        var n = Encoding.UTF8.GetBytes(nm, new Span<byte>(st.Dirent, DName - 1));
        st.Dirent[n] = 0;
        return st.Dirent;
    }

    /// <summary><c>closedir(dirp)</c> — release the token, its dirent buffer and
    /// state. Returns 0, or -1 (EBADF) for an unknown token.</summary>
    public static int closedir(void* dirp)
    {
        DirState? st;
        lock (_dirsLock)
        {
            if (!_dirs.Remove((nint)dirp, out st)) { errno = EBADF; return -1; }
        }
        NativeMemory.Free(st!.Dirent);
        NativeMemory.Free(dirp);
        return 0;
    }

    /// <summary><c>rewinddir(dirp)</c> — restart iteration from the first entry
    /// of the snapshot taken at <c>opendir</c>.</summary>
    public static void rewinddir(void* dirp)
    {
        lock (_dirsLock) { if (_dirs.TryGetValue((nint)dirp, out var st)) { st.Pos = 0; } }
    }

    // ---- <fcntl.h> open ----------------------------------------------------

    /// <summary><c>open(path, flags)</c> — see the 3-arg overload; the mode is
    /// only consulted when O_CREAT actually creates the file.</summary>
    public static int open(byte* path, int flags) => open(path, flags, 0);

    /// <summary><c>open</c> as chibi emits it — <c>flags</c>/<c>mode</c> arrive at
    /// the width its <c>sexp_sint_value</c> produced (<c>long</c>). open is
    /// variadic in C; this is the concrete arity dotcc resolves against.</summary>
    public static int open(byte* path, long flags, long mode) => open(path, (int)flags, (int)mode);

    /// <summary><c>open(path, flags, mode)</c> — open/create a file and return a
    /// dotcc fd (a FileSlot index, same space as <c>fileno</c>), or -1. The O_*
    /// access/creation flags (see &lt;fcntl.h&gt;) map to .NET FileMode/Access;
    /// O_CREAT applies <paramref name="mode"/> best-effort via chmod.</summary>
    public static int open(byte* path, int flags, int mode)
    {
        if (path == null) { errno = ENOENT; return -1; }
        var p = Encoding.UTF8.GetString(path, strlen(path));
        const int oCreat = 0x40, oExcl = 0x80, oTrunc = 0x200, oAppend = 0x400;
        var access = (flags & 0x3) switch
        {
            0x1 => FileAccess.Write,
            0x2 => FileAccess.ReadWrite,
            _ => FileAccess.Read,
        };
        FileMode fmode;
        if ((flags & oCreat) != 0 && (flags & oExcl) != 0) { fmode = FileMode.CreateNew; }
        else if ((flags & oCreat) != 0 && (flags & oTrunc) != 0) { fmode = FileMode.Create; }
        else if ((flags & oAppend) != 0) { fmode = (flags & oCreat) != 0 ? FileMode.Append : FileMode.Append; }
        else if ((flags & oTrunc) != 0) { fmode = FileMode.Truncate; }
        else if ((flags & oCreat) != 0) { fmode = FileMode.OpenOrCreate; }
        else { fmode = FileMode.Open; }
        // FileMode.Append demands write-only access in .NET.
        if (fmode == FileMode.Append) { access = FileAccess.Write; }
        try
        {
            var stream = new FileStream(p, fmode, access, FileShare.ReadWrite | FileShare.Delete);
            var fd = RegisterFileSlot(stream);
            if ((flags & oCreat) != 0 && mode != 0) { TrySetUnixMode(p, mode); }
            return fd;
        }
        catch (FileNotFoundException) { errno = ENOENT; return -1; }
        catch (DirectoryNotFoundException) { errno = ENOENT; return -1; }
        catch (IOException) when ((flags & oExcl) != 0 && File.Exists(p)) { errno = EEXIST; return -1; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
        catch (ArgumentException) { errno = EINVAL; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    // ---- path operations (<unistd.h> / <sys/stat.h> / <stdlib.h>) ----------

    /// <summary><c>mkdir(path, mode)</c> — create a directory; 0 on success, -1
    /// (EEXIST if it already exists, else EIO/EACCES). <paramref name="mode"/>
    /// is applied best-effort.</summary>
    public static int mkdir(byte* path, uint mode)
    {
        var p = Str(path);
        if (Directory.Exists(p) || File.Exists(p)) { errno = EEXIST; return -1; }
        try { Directory.CreateDirectory(p); TrySetUnixMode(p, (int)mode); return 0; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    /// <summary><c>rmdir(path)</c> — remove an empty directory; 0 on success, -1
    /// (ENOENT/ENOTDIR/EIO; a non-empty directory yields EEXIST, the closest
    /// errno dotcc carries to ENOTEMPTY).</summary>
    public static int rmdir(byte* path)
    {
        var p = Str(path);
        if (!Directory.Exists(p)) { errno = File.Exists(p) ? ENOTDIR : ENOENT; return -1; }
        try { Directory.Delete(p, recursive: false); return 0; }
        catch (IOException) { errno = EEXIST; return -1; }   // not empty
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
    }

    /// <summary><c>unlink(path)</c> — remove a file; 0 on success, -1
    /// (ENOENT/EACCES/EIO).</summary>
    public static int unlink(byte* path)
    {
        var p = Str(path);
        if (!File.Exists(p)) { errno = ENOENT; return -1; }
        try { File.Delete(p); return 0; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    /// <summary><c>chdir(path)</c> — change the working directory; 0 on success,
    /// -1 (ENOENT/EACCES).</summary>
    public static int chdir(byte* path)
    {
        var p = Str(path);
        if (!Directory.Exists(p)) { errno = ENOENT; return -1; }
        try { Directory.SetCurrentDirectory(p); return 0; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    /// <summary><c>getcwd(buf, size)</c> — copy the working directory into
    /// <paramref name="buf"/> (NUL-terminated) and return it, or <c>null</c>
    /// (ERANGE) if it doesn't fit. A <c>null</c> buf allocates a buffer (the GNU
    /// extension chibi relies on); the caller frees it.</summary>
    public static byte* getcwd(byte* buf, ulong size)
    {
        var cwd = Directory.GetCurrentDirectory();
        var need = Encoding.UTF8.GetByteCount(cwd) + 1;
        if (buf == null) { buf = (byte*)NativeMemory.Alloc((nuint)need); size = (ulong)need; }
        else if ((ulong)need > size) { errno = ERANGE; return null; }
        var n = Encoding.UTF8.GetBytes(cwd, new Span<byte>(buf, (int)size));
        buf[n] = 0;
        return buf;
    }

    /// <summary><c>chmod(path, mode)</c> — set Unix permission bits best-effort
    /// (a no-op success where the host doesn't support them). Returns 0, or -1
    /// (ENOENT) if the path doesn't exist.</summary>
    public static int chmod(byte* path, uint mode)
    {
        var p = Str(path);
        if (!File.Exists(p) && !Directory.Exists(p)) { errno = ENOENT; return -1; }
        TrySetUnixMode(p, (int)mode);
        return 0;
    }

    /// <summary><c>symlink(target, linkpath)</c> — create a symbolic link; 0 on
    /// success, -1 (EEXIST/EACCES/EIO).</summary>
    public static int symlink(byte* target, byte* linkpath)
    {
        var tgt = Str(target);
        var lnk = Str(linkpath);
        try { File.CreateSymbolicLink(lnk, tgt); return 0; }
        catch (IOException) when (File.Exists(lnk) || Directory.Exists(lnk)) { errno = EEXIST; return -1; }
        catch (UnauthorizedAccessException) { errno = EACCES; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    /// <summary><c>readlink(path, buf, bufsiz)</c> — write the symlink target
    /// (NOT NUL-terminated, per POSIX) into <paramref name="buf"/>, truncated to
    /// <paramref name="bufsiz"/>; returns the byte count, or -1 (EINVAL if the
    /// path is not a symlink).</summary>
    public static long readlink(byte* path, byte* buf, ulong bufsiz)
    {
        var p = Str(path);
        string? target;
        try { target = (File.Exists(p) ? new FileInfo(p) : (FileSystemInfo)new DirectoryInfo(p)).LinkTarget; }
        catch (IOException) { errno = EIO; return -1; }
        if (target == null) { errno = EINVAL; return -1; }
        var bytes = Encoding.UTF8.GetBytes(target);
        var n = Math.Min(bytes.Length, (int)bufsiz);
        new Span<byte>(bytes, 0, n).CopyTo(new Span<byte>(buf, n));
        return n;
    }

    /// <summary><c>link(oldpath, newpath)</c> — create a hard link
    /// <paramref name="newpath"/> to the existing file <paramref name="oldpath"/>.
    /// .NET has no managed hard-link API, so this forwards to the real OS
    /// primitive: <c>link(2)</c> on POSIX, <c>CreateHardLinkW</c> on Windows.
    /// 0 on success, -1 with <c>errno</c> set (EEXIST/ENOENT/EACCES/EXDEV/…) on
    /// failure.</summary>
    public static int link(byte* oldpath, byte* newpath)
    {
        if (OperatingSystem.IsWindows())
        {
            // CreateHardLinkW(newLink, existingFile, NULL): note the argument
            // order is the reverse of POSIX link(oldpath=existing, newpath=new).
            // `fixed` on a managed string pins its (NUL-terminated) UTF-16
            // buffer, so the W API gets a `char*` with no string marshalling —
            // the signature stays blittable and AOT-clean.
            var existing = Str(oldpath);
            var newLink = Str(newpath);
            fixed (char* e = existing)
            fixed (char* n = newLink)
            {
                if (CreateHardLinkW(n, e, null) != 0) { return 0; }
            }
            errno = Win32ToErrno(Marshal.GetLastWin32Error());
            return -1;
        }
        // POSIX link(2): the C strings pass straight through — already UTF-8 and
        // NUL-terminated, exactly what the syscall wants.
        if (PosixLink(oldpath, newpath) == 0) { return 0; }
        errno = Marshal.GetLastPInvokeError();
        return -1;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int PosixLink(byte* oldpath, byte* newpath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true)]
    private static extern int CreateHardLinkW(char* lpFileName, char* lpExistingFileName, void* lpSecurityAttributes);

    /// <summary>Map the handful of Win32 error codes the filesystem primitives
    /// raise onto the matching POSIX <c>errno</c> value, so cross-platform C code
    /// comparing <c>errno</c> against EEXIST/ENOENT/… behaves the same on Windows
    /// as on Unix. Unmapped codes fall back to EPERM ("operation not
    /// permitted").</summary>
    private static int Win32ToErrno(int err) => err switch
    {
        2 or 3 => ENOENT,    // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
        5 => EACCES,         // ERROR_ACCESS_DENIED
        17 => EXDEV,         // ERROR_NOT_SAME_DEVICE
        80 or 183 => EEXIST, // ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS
        _ => EPERM,
    };

    /// <summary><c>realpath(path, resolved)</c> — canonicalize an EXISTING path;
    /// writes into <paramref name="resolved"/> (or allocates when it's
    /// <c>null</c>, the GNU/POSIX.1-2008 form) and returns it, or <c>null</c>
    /// (ENOENT) if the path doesn't exist.</summary>
    public static byte* realpath(byte* path, byte* resolved)
    {
        var p = Str(path);
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception) { errno = EINVAL; return null; }
        if (!File.Exists(full) && !Directory.Exists(full)) { errno = ENOENT; return null; }
        var need = Encoding.UTF8.GetByteCount(full) + 1;
        if (resolved == null) { resolved = (byte*)NativeMemory.Alloc((nuint)need); }
        var n = Encoding.UTF8.GetBytes(full, new Span<byte>(resolved, need));
        resolved[n] = 0;
        return resolved;
    }

    // ---- <sys/file.h> ------------------------------------------------------

    /// <summary><c>flock(fd, operation)</c> — advisory locking; a success no-op
    /// (see the class remarks). Always 0.</summary>
    public static int flock(int fd, int operation) => 0;

    // ---- more <unistd.h> ---------------------------------------------------

    /// <summary><c>access(path, mode)</c> — 0 when the path exists and the
    /// requested access is plausible (W_OK denied on a read-only file), else -1
    /// (ENOENT/EACCES). X_OK/R_OK can't be checked portably, so existence
    /// suffices for them.</summary>
    public static int access(byte* path, int mode)
    {
        var p = Str(path);
        var isFile = File.Exists(p);
        if (!isFile && !Directory.Exists(p)) { errno = ENOENT; return -1; }
        const int wOk = 2;
        if ((mode & wOk) != 0 && isFile)
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReadOnly) != 0) { errno = EACCES; return -1; } }
            catch (IOException) { errno = EIO; return -1; }
        }
        return 0;
    }

    /// <summary><c>chown(path, owner, group)</c> — no-op success (managed hosts
    /// don't expose uid/gid changes portably); -1 (ENOENT) for a missing path.
    /// Best-effort, mirroring <see cref="chmod"/>.</summary>
    public static int chown(byte* path, uint owner, uint group)
    {
        var p = Str(path);
        if (!File.Exists(p) && !Directory.Exists(p)) { errno = ENOENT; return -1; }
        return 0;
    }

    /// <summary><c>ftruncate(fd, length)</c> — resize the fd's backing file to
    /// <paramref name="length"/> bytes; 0 on success, -1 (EBADF/EINVAL).</summary>
    public static int ftruncate(int fd, long length)
    {
        var s = SlotByFd(fd);
        if (s?.Stream is not { CanSeek: true, CanWrite: true } st) { errno = EBADF; return -1; }
        try { st.SetLength(length); return 0; }
        catch (ArgumentOutOfRangeException) { errno = EINVAL; return -1; }
        catch (IOException) { errno = EIO; return -1; }
    }

    /// <summary><c>mkfifo(path, mode)</c> — named pipes have no portable .NET
    /// API; fail with EPERM (honest). Defined so <c>(chibi filesystem)</c> links;
    /// the R7RS suite never calls it.</summary>
    public static int mkfifo(byte* path, uint mode)
    {
        errno = EPERM;
        return -1;
    }

    /// <summary><c>pipe(pipefd)</c> — anonymous pipes aren't modeled in dotcc's
    /// FileSlot fd space; fail with EPERM. Defined so the module links; unused by
    /// the R7RS suite.</summary>
    public static int pipe(int* pipefd)
    {
        errno = EPERM;
        return -1;
    }

    /// <summary><c>dup(fd)</c> — fd duplication isn't modeled (a dotcc fd owns its
    /// backing stream; two fds sharing one stream has no clean close semantics);
    /// fail with EPERM. Defined so the module links; unused by the R7RS suite.</summary>
    public static int dup(int fd)
    {
        errno = EPERM;
        return -1;
    }

    /// <inheritdoc cref="dup(int)"/>
    public static int dup2(int oldfd, int newfd)
    {
        errno = EPERM;
        return -1;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Decode a NUL-terminated UTF-8 C string to a managed string
    /// (empty for <c>null</c>).</summary>
    private static string Str(byte* s) => s == null ? string.Empty : Encoding.UTF8.GetString(s, strlen(s));

    /// <summary>Apply Unix permission bits where the host supports them; ignore
    /// failure (a path that vanished) — chmod is best-effort. On Windows there
    /// are no Unix mode bits, so it's a no-op: <see cref="File.SetUnixFileMode(string, UnixFileMode)"/>
    /// is unsupported there (the explicit OS guard both expresses that and
    /// satisfies CA1416, rather than catching the PlatformNotSupportedException).</summary>
    private static void TrySetUnixMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) { return; }
        try { File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF)); }
        catch { /* a path that vanished mid-call — best-effort */ }
    }
}

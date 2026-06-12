#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DotCC.Libc;

/// <summary>
/// The POSIX dynamic-loader surface (<c>&lt;dlfcn.h&gt;</c>):
/// <c>dlopen</c>/<c>dlsym</c>/<c>dlclose</c>/<c>dlerror</c>, lowered onto .NET's
/// <see cref="NativeLibrary"/> (AOT-clean and cross-platform — the same four
/// calls back a Linux <c>.so</c>, a Windows <c>.dll</c> and a macOS
/// <c>.dylib</c>). This is the *consume* direction that complements
/// <c>-shared</c>: a dotcc program can load a real native library at runtime and
/// call its exports — the loader half of a plugin host (chibi/Lua), and the way a
/// dotcc program consumes a dotcc-built <c>-shared</c> library.
///
/// <para>The calling-convention contract: <c>dlsym</c> returns a NATIVE code
/// address, which must be invoked through a <c>delegate* unmanaged[Cdecl]</c>.
/// dotcc recognises the POSIX idiom — casting the <c>dlsym(...)</c> result
/// DIRECTLY to a function-pointer type — and marks that function type native
/// (<c>CType.Func.IsNativeCallConv</c>), so the emitted <c>calli</c> uses the C
/// calling convention. Laundering the address through a <c>void*</c> variable
/// defeats the recognition and draws a compile-time warning; see
/// <c>include/dlfcn.h</c>.</para>
/// </summary>
public static partial class Libc
{
    // dlerror state. POSIX: dlerror() returns the message from the most recent
    // failed dl* call, then CLEARS the indicator (a second immediate call returns
    // NULL); it returns NULL when there has been no error since the last call. The
    // message is handed back as a stable char* in a reused per-thread native
    // buffer — no GC allocation behind the returned pointer (same posture as
    // getenv/strerror's thread-local C-string buffers).
    [ThreadStatic] private static string? _dlError;
    [ThreadStatic] private static unsafe byte* _dlErrBuf;
    [ThreadStatic] private static int _dlErrCap;

    /// <summary>Record a dl* failure message for the next <see cref="dlerror"/>.</summary>
    private static void SetDlError(string message) => _dlError = message;

    /// <summary><c>dlopen(filename, flag)</c> — load a shared object and return an
    /// opaque handle. A NULL (or empty) <paramref name="filename"/> yields a handle
    /// for the main program (POSIX <c>dlopen(NULL, …)</c>) via
    /// <see cref="NativeLibrary.GetMainProgramHandle"/>. The <c>RTLD_*</c>
    /// <paramref name="flag"/> bits are accepted and ignored — the platform loader's
    /// default binding is used. On failure returns NULL and sets the
    /// <see cref="dlerror"/> message.</summary>
    public static unsafe void* dlopen(byte* filename, int flag)
    {
        if (filename == null || *filename == 0)
        {
            return (void*)NativeLibrary.GetMainProgramHandle();
        }
        string path = Str(filename);
        if (NativeLibrary.TryLoad(path, out IntPtr handle))
        {
            return (void*)handle;
        }
        SetDlError(path + ": cannot open shared object file: No such file or directory");
        return null;
    }

    /// <summary><c>dlsym(handle, symbol)</c> — resolve <paramref name="symbol"/> in
    /// <paramref name="handle"/> to its address. On failure returns NULL and sets
    /// the <see cref="dlerror"/> message. The returned address is NATIVE code —
    /// cast it directly to a function-pointer type to call it (see the class
    /// remarks / <c>&lt;dlfcn.h&gt;</c>).</summary>
    public static unsafe void* dlsym(void* handle, byte* symbol)
    {
        string name = Str(symbol);
        if (NativeLibrary.TryGetExport((IntPtr)handle, name, out IntPtr addr))
        {
            return (void*)addr;
        }
        SetDlError("undefined symbol: " + name);
        return null;
    }

    /// <summary><c>dlclose(handle)</c> — release a handle from <see cref="dlopen"/>.
    /// Returns 0 (POSIX success). Routes to <see cref="NativeLibrary.Free"/>; a NULL
    /// handle (e.g. the main-program handle, or a failed dlopen) is left alone.</summary>
    public static unsafe int dlclose(void* handle)
    {
        if (handle != null) { NativeLibrary.Free((IntPtr)handle); }
        return 0;
    }

    /// <summary><c>dlerror()</c> — the message for the most recent failed dl* call,
    /// or NULL if there has been none since the last <c>dlerror</c> call. Reading it
    /// CLEARS the indicator (POSIX), so an immediate second call returns NULL. The
    /// message lives in a reused per-thread native buffer (stable <c>char*</c>).</summary>
    public static unsafe byte* dlerror()
    {
        string? msg = _dlError;
        if (msg is null) { return null; }
        _dlError = null;  // POSIX: reading dlerror clears the error indicator.
        int need = Encoding.UTF8.GetByteCount(msg) + 1;
        if (_dlErrBuf == null || _dlErrCap < need)
        {
            if (_dlErrBuf != null) { NativeMemory.Free(_dlErrBuf); }
            _dlErrCap = need;
            _dlErrBuf = (byte*)NativeMemory.Alloc((nuint)need);
        }
        int n = Encoding.UTF8.GetBytes(msg, new Span<byte>(_dlErrBuf, _dlErrCap));
        _dlErrBuf[n] = 0;
        return _dlErrBuf;
    }
}

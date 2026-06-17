#nullable enable

using System;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the POSIX dynamic-loader surface (<see cref="DotCC.Libc"/>'s
/// DlfcnLib): <c>dlopen</c>/<c>dlsym</c>/<c>dlclose</c>/<c>dlerror</c> over .NET's
/// <c>NativeLibrary</c>. These call the runtime directly (the recognition that
/// emits a <c>delegate* unmanaged[Cdecl]</c> for a dlsym cast is exercised by the
/// <c>dlopen-strlen</c> functional fixture). A symbol resolved by <c>dlsym</c> is
/// invoked through an unmanaged cdecl function pointer here, proving the loaded
/// native code runs and returns correctly on both host OSes.
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcDlfcnTests
{
    // RTLD_LAZY — accepted and ignored by dotcc's dlopen (platform default binding),
    // passed for realism.
    private const int RTLD_LAZY = 1;

    // void* is not a legal generic type argument (CS0306), so pointer identity is
    // asserted as nint.
    private static nint N(void* p) => (nint)p;

    /// <summary>Open a real system C library: <c>libc.so.6</c> on Linux,
    /// <c>msvcrt.dll</c> on Windows (both export the CRT's <c>strlen</c>). The same
    /// runtime probing a dotcc-emitted program would do — dotcc defines no OS macro.</summary>
    private static void* OpenSystemC()
    {
        void* h;
        fixed (byte* p = "libc.so.6\0"u8) { h = dlopen(p, RTLD_LAZY); }
        if (h == null) { fixed (byte* p = "msvcrt.dll\0"u8) { h = dlopen(p, RTLD_LAZY); } }
        return h;
    }

    private static void* Sym(void* h, ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) { return dlsym(h, p); }
    }

    private static string? ErrText(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    [Fact]
    public void dlopen_then_dlsym_resolves_strlen_and_the_native_call_runs()
    {
        void* h = OpenSystemC();
        N(h).ShouldNotBe(0, "the host's system C library should load");

        var strlen = (delegate* unmanaged[Cdecl]<byte*, nuint>)Sym(h, "strlen\0"u8);
        N(strlen).ShouldNotBe(0, "strlen is exported by libc.so.6 / msvcrt.dll");

        nuint len;
        fixed (byte* msg = "hello\0"u8) { len = strlen(msg); }
        ((int)len).ShouldBe(5);   // the loaded NATIVE strlen ran through the cdecl call

        dlclose(h).ShouldBe(0);
    }

    [Fact]
    public void dlopen_null_returns_the_main_program_handle()
    {
        // POSIX dlopen(NULL, …) → a handle to the main program (NativeLibrary's
        // GetMainProgramHandle). Not dlclose'd — releasing the main handle is
        // pointless and best left alone.
        void* h = dlopen(null, RTLD_LAZY);
        N(h).ShouldNotBe(0);
    }

    [Fact]
    public void dlopen_missing_library_returns_null_and_dlerror_names_it_once()
    {
        _ = dlerror();   // drain any prior thread-local error (ThreadStatic state)

        void* h;
        fixed (byte* p = "dotcc-no-such-lib.so\0"u8) { h = dlopen(p, RTLD_LAZY); }
        N(h).ShouldBe(0);

        string? err = ErrText(dlerror());
        err.ShouldNotBeNull();
        err!.ShouldContain("dotcc-no-such-lib.so");
        // POSIX: reading dlerror clears the indicator — an immediate second call is null.
        ErrText(dlerror()).ShouldBeNull();
    }

    [Fact]
    public void dlsym_unknown_symbol_returns_null_and_dlerror_names_it()
    {
        _ = dlerror();
        void* h = OpenSystemC();
        N(h).ShouldNotBe(0);

        void* fn = Sym(h, "dotcc_no_such_symbol\0"u8);
        N(fn).ShouldBe(0);
        string? err = ErrText(dlerror());
        err.ShouldNotBeNull();
        err!.ShouldContain("dotcc_no_such_symbol");

        dlclose(h).ShouldBe(0);
    }

    [Fact]
    public void dlerror_is_null_when_there_has_been_no_error()
    {
        _ = dlerror();                       // drain
        ErrText(dlerror()).ShouldBeNull();   // and now it stays null
    }
}

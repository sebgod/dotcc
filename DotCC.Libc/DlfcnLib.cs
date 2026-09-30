#nullable enable

using System;
using System.Collections.Concurrent;
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
        string path = HostPath(filename);
        if (IsManagedAssembly(path))
        {
            return DlOpenAssembly(path);
        }
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
    /// remarks / <c>&lt;dlfcn.h&gt;</c>). In a .NET assembly from <see cref="dlopen"/>
    /// that is the function's <c>[UnmanagedCallersOnly]</c> export wrapper.</summary>
    public static unsafe void* dlsym(void* handle, byte* symbol)
    {
        string name = Str(symbol);
        DlAssembly? asm;
        lock (_dlAssemblyLock) { _dlAssemblies.TryGetValue((nint)handle, out asm); }
        if (asm is not null)
        {
            if (asm.Symbols.TryGetValue(name, out var entry)) { return (void*)entry.Native; }
        }
        else if (NativeLibrary.TryGetExport((IntPtr)handle, name, out IntPtr addr))
        {
            return (void*)addr;
        }
        SetDlError("undefined symbol: " + name);
        return null;
    }

    /// <summary><c>dlclose(handle)</c> — release a handle from <see cref="dlopen"/>.
    /// Returns 0 (POSIX success). Routes to <see cref="NativeLibrary.Free"/>; a NULL
    /// handle (e.g. the main-program handle, or a failed dlopen) is left alone. A .NET
    /// assembly stays loaded (it is never unloaded, see <see cref="DlOpenAssembly"/>), so
    /// its handle stays valid for a later <c>dlopen</c> of the same file.</summary>
    public static unsafe int dlclose(void* handle)
    {
        bool managed;
        lock (_dlAssemblyLock) { managed = _dlAssemblies.ContainsKey((nint)handle); }
        if (!managed && handle != null) { NativeLibrary.Free((IntPtr)handle); }
        return 0;
    }

    /// <summary>A .NET assembly <see cref="dlopen"/> loaded: its exports by C name, each
    /// with its native entry point (the <c>[UnmanagedCallersOnly]</c> wrapper) and the
    /// managed function it wraps.</summary>
    private sealed class DlAssembly(Dictionary<string, (nint Native, nint Managed)> symbols)
    {
        public Dictionary<string, (nint Native, nint Managed)> Symbols { get; } = symbols;
    }

    private static readonly object _dlAssemblyLock = new();
    // Handle to assembly, and full path to handle. A handle is odd (a tagged count), so it
    // never equals a native loader's handle, which is an aligned address.
    private static readonly Dictionary<nint, DlAssembly> _dlAssemblies = new();
    private static readonly Dictionary<string, nint> _dlAssemblyHandles = new(StringComparer.Ordinal);
    // Native entry point to managed function, over every assembly loaded; read by ManagedEntry.
    private static readonly ConcurrentDictionary<nint, nint> _dlManagedEntries = new();

    /// <summary>Whether <paramref name="path"/> names a .NET assembly (a PE file with
    /// metadata), which <see cref="dlopen"/> loads as managed code, rather than a native
    /// library. A NativeAOT-published library is native (it has no metadata).</summary>
    private static bool IsManagedAssembly(string path)
    {
        if (!File.Exists(path)) { return false; }
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Load the .NET assembly at <paramref name="path"/> for <see cref="dlopen"/> and read its
    /// export table: the <c>__dotcc_exports</c> method a dotcc managed library
    /// (<c>-shared -fassembly</c>) carries, one entry per function with external linkage.
    /// The assembly loads into this runtime's own load context, so its references to a
    /// library the program already uses (libpython, say) bind to the loaded copy, whose
    /// runtime and statics it then shares. That context is not collectible (statics are
    /// addressed by pointer), so the assembly is never unloaded. A NativeAOT program cannot
    /// load an assembly at all, so there the call fails, and the library has to be linked in
    /// statically instead.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "A dlopened assembly is not part of the trimmed program; a NativeAOT program refuses it before loading.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The export table method is looked up on the dlopened assembly's own public types.")]
    private static unsafe void* DlOpenAssembly(string path)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            SetDlError(path + ": a .NET assembly cannot be loaded into a NativeAOT program; link it in statically");
            return null;
        }
        string full = Path.GetFullPath(path);
        lock (_dlAssemblyLock)
        {
            if (_dlAssemblyHandles.TryGetValue(full, out var existing)) { return (void*)existing; }
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Libc).Assembly)
                          ?? System.Runtime.Loader.AssemblyLoadContext.Default;
            System.Reflection.Assembly assembly;
            try { assembly = context.LoadFromAssemblyPath(full); }
            catch (Exception e) when (e is FileLoadException or BadImageFormatException or FileNotFoundException)
            {
                SetDlError(path + ": cannot load .NET assembly: " + e.Message);
                return null;
            }
            var symbols = new Dictionary<string, (nint Native, nint Managed)>(StringComparer.Ordinal);
            foreach (var type in assembly.GetExportedTypes())
            {
                var table = type.GetMethod("__dotcc_exports",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, Type.EmptyTypes);
                if (table?.Invoke(null, null) is not ValueTuple<string, nint, nint>[] entries) { continue; }
                foreach (var (name, native, managed) in entries)
                {
                    symbols[name] = (native, managed);
                    _dlManagedEntries[native] = managed;
                }
            }
            nint handle = (nint)(((long)_dlAssemblies.Count + 1) * 2 + 1);
            _dlAssemblies[handle] = new DlAssembly(symbols);
            _dlAssemblyHandles[full] = handle;
            return (void*)handle;
        }
    }

    /// <summary>
    /// The managed function behind <paramref name="entry"/>, when it is the native entry point
    /// <see cref="dlsym"/> handed out for a function of a .NET assembly; any other address comes
    /// back unchanged. A C program converts a <c>dlsym</c> result to one of its own function
    /// pointer types (CPython's <c>dl_funcptr</c>, say) and calls it through that type later, so
    /// dotcc routes a conversion from a C-convention function pointer to a managed one through
    /// here: the call then reaches the function directly, not its
    /// <c>[UnmanagedCallersOnly]</c> wrapper, which managed code cannot call.
    /// </summary>
    public static unsafe void* ManagedEntry(void* entry) =>
        _dlManagedEntries.TryGetValue((nint)entry, out var managed) ? (void*)managed : entry;

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

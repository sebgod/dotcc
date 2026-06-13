#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DotCC.Libc;

/// <summary>
/// Native-library loader for dotcc IMPORT MODE (<c>-l&lt;name&gt;</c> / <c>-L&lt;dir&gt;</c>).
/// Mirrors <c>ld.so</c>: load each <c>-l</c> library in command-line order, then bind
/// every undefined prototype to the FIRST library that exports it. Backs the emitted
/// <c>DotCcImports</c> GOT-style function-pointer table (see <c>Compiler.RenderImportsClass</c>).
/// </summary>
/// <remarks>
/// NOT part of <see cref="Libc"/>: it is a sibling helper the runtime splice lifts into
/// every emitted program (namespace stripped, referenced by bare name) exactly like
/// <c>Float128</c>. Built on <see cref="NativeLibrary"/>, so it works identically under a
/// plain <c>dotnet run</c> and a NativeAOT publish — no <c>[DllImport]</c> stub per symbol
/// (which couldn't attribute a symbol to one of several <c>-l</c> libraries anyway).
/// </remarks>
public static class NativeImports
{
    /// <summary>
    /// Load a native library by its <c>-l</c> name. Probes each <paramref name="searchDirs"/>
    /// (<c>-L</c>) entry with the platform's filename conventions
    /// (<c>lib&lt;name&gt;.so</c> / <c>&lt;name&gt;.so</c> on Linux,
    /// <c>&lt;name&gt;.dll</c> / <c>lib&lt;name&gt;.dll</c> on Windows,
    /// <c>lib&lt;name&gt;.dylib</c> / <c>&lt;name&gt;.dylib</c> on macOS), then falls back to
    /// the OS default search (system dirs, <c>LD_LIBRARY_PATH</c> / <c>PATH</c>) by variant
    /// and finally the bare name. Returns <see cref="IntPtr.Zero"/> if nothing loads — the
    /// caller decides whether a missing symbol from this handle is fatal.
    /// </summary>
    public static IntPtr LoadLibrary(string name, string[] searchDirs)
    {
        var variants = NameVariants(name);
        // 1) Explicit -L dirs, platform-decorated names, in dir order.
        foreach (var dir in searchDirs)
        {
            foreach (var variant in variants)
            {
                var candidate = Path.Combine(dir, variant);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) { return h; }
            }
        }
        // 2) OS default search by each decorated variant, then the bare name
        //    (NativeLibrary also applies its own platform decoration to a bare name).
        foreach (var variant in variants)
        {
            if (NativeLibrary.TryLoad(variant, out var h)) { return h; }
        }
        return NativeLibrary.TryLoad(name, out var hn) ? hn : IntPtr.Zero;
    }

    /// <summary>Platform filename forms for an <c>-l</c> name, most-specific first.
    /// No <c>.so.6</c>-style ABI-version guessing — that is the user's to spell.</summary>
    private static string[] NameVariants(string name)
    {
        if (OperatingSystem.IsWindows()) { return new[] { name + ".dll", "lib" + name + ".dll" }; }
        if (OperatingSystem.IsMacOS()) { return new[] { "lib" + name + ".dylib", name + ".dylib" }; }
        return new[] { "lib" + name + ".so", name + ".so" }; // Linux / other Unix
    }

    /// <summary>
    /// Resolve <paramref name="symbol"/> against <paramref name="handles"/> in order,
    /// returning the address from the FIRST library that exports it (ld.so first-wins).
    /// Zero handles (a library that failed to load) are skipped. Returns false if no
    /// loaded library exports the symbol.
    /// </summary>
    public static unsafe bool TryResolveExport(IntPtr[] handles, string symbol, out void* fn)
    {
        foreach (var h in handles)
        {
            if (h != IntPtr.Zero && NativeLibrary.TryGetExport(h, symbol, out var addr))
            {
                fn = (void*)addr;
                return true;
            }
        }
        fn = null;
        return false;
    }
}

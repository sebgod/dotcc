#nullable enable

using System.Collections.Generic;

namespace DotCC.Ir;

/// <summary>The top-level type names the spliced runtime (<c>DotCC.Libc</c>, see
/// <c>Compiler.LoadRuntimeBlock</c>) declares in the emitted program's GLOBAL namespace. A user type
/// emitted under one of these names would collide with the runtime's own (C# CS0101 — the emitted
/// program would transpile "successfully" and then not build), so every front-end has to keep its type
/// names off this list: the Zig front-end QUALIFIES a colliding root-module container
/// (<c>root__Allocator</c>, invisible to the program, which still spells <c>Allocator</c>), and the IR's
/// aggregate registries refuse one loudly — which is what the C front-end reaches, since renaming a C tag
/// would mean rewriting every reference to it.
/// <para>Kept in sync with the runtime by <c>RuntimeTypeNamesTests</c>, which reflects over the compiled
/// <c>DotCC.Libc</c> (the same sources that are spliced) for its non-nested types and fails on any drift in
/// either direction. Types NESTED in <c>Libc</c> (<c>VaList</c>, <c>VaArg</c>) are not top-level, so they
/// are not reserved.</para></summary>
internal static class RuntimeTypeNames
{
    private static readonly HashSet<string> Names = new(System.StringComparer.Ordinal)
    {
        "Alignment", "Allocator", "AllocatorVTable", "ArenaAllocator", "ArenaChunk", "Atomic", "CBool",
        "ConstSlice", "ErrUnion", "FixedBufferAllocator", "Float128", "Libc", "NativeImports",
        "PrintfBuilder", "ScanfReader", "Slice", "SprintfBuilder", "Unit",
        "WSprintfBuilder", "ZigAlloc", "ZigErrorReturn", "ZigList", "ZigMath", "ZigMem", "ZigTesting",
    };

    /// <summary>The classes the C# program SHELL emits around the user code (<c>CSharpBackend.BuildShell</c>
    /// and the emit modes) — reserved for the same reason, but not part of the runtime sources the sync
    /// test scans.</summary>
    private static readonly HashSet<string> ShellNames = new(System.StringComparer.Ordinal)
    {
        "Cond", "DotCcProgram", "DotCcGlobals", "DotCcExports", "DotCcImports", "DotCcStaticImports", "DotCcLib",
    };

    /// <summary>The runtime-declared set, for the sync test (the shell names are separate).</summary>
    internal static IReadOnlyCollection<string> RuntimeDeclared => Names;

    /// <summary>True when <paramref name="name"/> is a type the runtime or the program shell declares at
    /// the top level of the emitted program.</summary>
    internal static bool IsReserved(string name) => Names.Contains(name) || ShellNames.Contains(name);
}

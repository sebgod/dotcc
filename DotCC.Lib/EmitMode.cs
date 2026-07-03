#nullable enable

namespace DotCC;

/// <summary>
/// The shape of the output <see cref="Compiler.EmitCSharp"/> produces — the single
/// knob that replaced the old mutually-exclusive <c>fileBased</c> / <c>libraryMode</c>
/// / <c>asObject</c> boolean trio (in every real combination exactly one was set, so
/// one enum states the intent directly and rules out the nonsensical pairings by
/// construction). Mirrors the frontend's <c>EmitKind</c>, minus <c>Build</c> — that is
/// a CLI action ("emit a csproj, then run <c>dotnet build</c>"), not a distinct output
/// shape; it produces the same <see cref="Csproj"/> source.
/// </summary>
/// <remarks>
/// This is the OUTPUT-shape axis only. Orthogonal codegen toggles that compose with any
/// shape (e.g. <c>-fsanitize=address</c>'s <c>debugHeap</c>) stay their own parameters —
/// folding them in here would multiply the value set combinatorially.
/// </remarks>
public enum EmitMode
{
    /// <summary>A single .NET 10 file-based program carrying the
    /// <c>#:property AllowUnsafeBlocks=true</c> header (the default; <c>--emit=file</c>).</summary>
    File,

    /// <summary><c>Program.cs</c> paired with a generated csproj (<c>--emit=csproj</c> /
    /// <c>--emit=build</c> / <c>-c</c>) — the standalone-executable shell, no <c>#:property</c>
    /// header.</summary>
    Csproj,

    /// <summary>A NativeAOT-publishable shared-library shell (<c>-shared</c>): user functions
    /// live in <c>internal static class DotCcLib</c>, and non-static C functions get a matching
    /// <c>[UnmanagedCallersOnly]</c> wrapper in <c>public static class DotCcExports</c>. Always
    /// csproj-shaped, and needs no <c>main</c>.</summary>
    SharedLib,

    /// <summary>A per-TU object fragment (<c>--emit=obj</c>): functions + this TU's type decls
    /// + globals with link markers, no shell or runtime — merged later by
    /// <see cref="Compiler.LinkObjects"/>.</summary>
    Object,
}

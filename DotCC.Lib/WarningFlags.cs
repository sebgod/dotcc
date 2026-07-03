#nullable enable

namespace DotCC;

/// <summary>
/// dotcc's compiler diagnostics as a single bit set, threaded as one value through
/// the whole pipeline (CLI → <see cref="Compiler.EmitCSharp"/> → <c>FrontendRequest</c>
/// → <c>IrBuilder</c> / the codegen gates / the dialect gate) instead of one
/// <c>bool</c> parameter per warning at every layer — the accretion that made the old
/// <c>warn*</c> / <c>pedantic*</c> passthrough unwieldy. Each member mirrors a gcc/clang
/// <c>-W</c> flag; the defaults match those compilers (only the const-discard warning is
/// on by default, see <see cref="Default"/>).
/// </summary>
[System.Flags]
public enum WarningFlags
{
    /// <summary>No warnings.</summary>
    None = 0,

    /// <summary>gcc <c>-Wdiscarded-qualifiers</c> — an implicit conversion that drops a
    /// pointee <c>const</c>. ON by default (part of <see cref="Default"/>); the CLI's
    /// <c>-Wno-discarded-qualifiers</c> clears this bit.</summary>
    DiscardedQualifiers = 1 << 0,

    /// <summary>gcc/clang <c>-Wconversion</c> — an implicit integer conversion that
    /// narrows (a wider value stored into a narrower type). Opt-in (off by default).</summary>
    Conversion = 1 << 1,

    /// <summary>gcc/clang <c>-Wimplicit-fallthrough</c> — a non-empty <c>switch</c> case
    /// that falls through to the next label without a <c>[[fallthrough]];</c> marker.
    /// Opt-in (off by default).</summary>
    ImplicitFallthrough = 1 << 2,

    /// <summary>gcc <c>-Wpedantic</c> / <c>-pedantic</c> — enable dialect-conformance
    /// gating (a feature newer than the selected <c>-std=</c> is diagnosed). Opt-in;
    /// drives the <c>DialectGate</c> pass. Escalate with <see cref="AsErrors"/> /
    /// <see cref="PedanticErrors"/>.</summary>
    Pedantic = 1 << 3,

    /// <summary>Escalate diagnostics to errors (gcc's <c>-Werror</c> family). Today
    /// only the pedantic dialect gate consults it — set together with
    /// <see cref="Pedantic"/> via <see cref="PedanticErrors"/> to model
    /// <c>-pedantic-errors</c> (collect-all, fail once). A modifier bit: meaningless
    /// on its own.</summary>
    AsErrors = 1 << 4,

    /// <summary>gcc <c>-pedantic-errors</c> — the named composite: enable pedantic
    /// gating AND treat its violations as errors.</summary>
    PedanticErrors = Pedantic | AsErrors,

    /// <summary>The compiler default — only the on-by-default warnings
    /// (<see cref="DiscardedQualifiers"/>).</summary>
    Default = DiscardedQualifiers,
}

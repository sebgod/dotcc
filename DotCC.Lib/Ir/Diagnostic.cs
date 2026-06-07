#nullable enable

namespace DotCC.Ir;

/// <summary>Diagnostic severity. Errors fail the compile (collect-all, like
/// <c>-pedantic-errors</c>); warnings print to stderr and continue.</summary>
public enum Severity { Warning, Error }

/// <summary>
/// A semantic diagnostic with a C source location — the clang-style reporting
/// surface the legacy emitter can't produce (it only leaks Roslyn CS#### errors
/// from the generated C# downstream). Collected on the
/// <see cref="TranslationUnit"/> by <see cref="IrBuilder"/> and the semantic
/// passes; rendered as <c>file:line:col: severity: message</c>.
/// </summary>
public sealed record Diagnostic(Severity Severity, string Message, SrcPos Pos, string? File = null)
{
    public override string ToString()
    {
        var loc = File is null ? Pos.ToString() : $"{File}:{Pos}";
        var sev = Severity == Severity.Error ? "error" : "warning";
        return $"{loc}: {sev}: {Message}";
    }
}

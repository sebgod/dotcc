#nullable enable

using System;
using System.IO;

namespace DotCC;

/// <summary>Where a compile's warnings go. One concern of <see cref="Compiler"/>.</summary>
public static partial class Compiler
{
    /// <summary>
    /// The writer dotcc prints its warnings and notes to (<c>dotcc: warning: …</c>, the preprocessor's
    /// <c>#warning</c>, import mode's skipped candidates), or <see langword="null"/> (the default) for
    /// <see cref="Console.Error"/>, read at each write so a caller that swaps <c>Console.Error</c> still
    /// captures them. A host that runs compiles in parallel with code that redirects the console (a test
    /// suite capturing a program's stderr) points this elsewhere, so one compile's warning cannot land in
    /// another's capture. Errors are not written here: they are thrown as <see cref="CompileException"/>.
    /// </summary>
    public static TextWriter? DiagnosticsWriter { get; set; }

    /// <summary>The writer in effect for a warning written now (see <see cref="DiagnosticsWriter"/>).</summary>
    internal static TextWriter Diagnostics => DiagnosticsWriter ?? Console.Error;
}

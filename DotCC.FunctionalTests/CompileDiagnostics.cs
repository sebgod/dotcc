#nullable enable

using System.IO;
using System.Runtime.CompilerServices;

namespace DotCC.FunctionalTests;

/// <summary>Keeps dotcc's compile warnings off the process console for this assembly.</summary>
internal static class CompileDiagnostics
{
    /// <summary>
    /// Test classes here run in parallel, and a few capture a program's stderr by redirecting
    /// <c>Console.Error</c> (FixtureRunner.CompileAndRunCapturingStreams, under its console lock). A
    /// fixture compiled meanwhile by another class used to print its <c>dotcc: warning:</c> lines to that
    /// same <c>Console.Error</c>, outside the lock, so they landed in the capture and an empty-stderr
    /// assertion failed (ManagedLibraryTests, a <c>strlen</c> const-discard warning from an unrelated
    /// fixture). No test in this assembly asserts on compile warnings (the unit suite does, serialized),
    /// so they go nowhere.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => Compiler.DiagnosticsWriter = TextWriter.Null;
}

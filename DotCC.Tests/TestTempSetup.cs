#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace DotCC.Tests;

/// <summary>
/// Redirects the test process's temp directory to a clean, dedicated subdirectory before any
/// test runs.
/// </summary>
/// <remarks>
/// The unit tests write each inline program to a temp file and compile it in-process. The C
/// front-end's include resolution scans the INPUT FILE'S DIRECTORY recursively (to resolve
/// sibling/quoted <c>#include "foo.h"</c> and subdir-qualified includes). When the input is
/// written to the shared system temp ROOT, that walk traverses the whole machine's temp tree
/// (often tens of thousands of unrelated entries) on EVERY C compile — turning a &lt;2&#160;ms
/// compile into well over a second, and the full unit suite into many minutes (with heavy GC
/// from the path-string churn). Pointing <c>TMP</c>/<c>TEMP</c> at a clean per-process subdir
/// keeps the walk to just the file under test. (Zig compiles build no include map, so they were
/// never affected — which is exactly how this was diagnosed: Zig ~2&#160;ms vs C ~1.9&#160;s for
/// the same trivial program.)
/// </remarks>
internal static class TestTempSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Resolve the ORIGINAL temp root first (before the override below), then nest a clean,
        // process-unique subdirectory under it so the include scan only ever sees test files.
        var baseTemp = Path.GetTempPath();
        var clean = Path.Combine(baseTemp, $"dotcc-tests-{Environment.ProcessId}");
        Directory.CreateDirectory(clean);
        Environment.SetEnvironmentVariable("TMP", clean);
        Environment.SetEnvironmentVariable("TEMP", clean);
    }
}

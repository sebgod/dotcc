#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// <c>-fsanitize=address</c> routes the emitted program's malloc/calloc/realloc/
/// free through the checked debug heap (redzone overflow + bad/double-free
/// detection — see <c>DotCC.Libc.Libc</c>'s debug-heap block). The flag makes
/// the shell call <c>Libc.EnableDebugHeap()</c> once at startup, before any
/// allocation; without it the call is absent and malloc/free stay on the plain
/// <c>NativeMemory</c> route (the runtime <c>DOTCC_DEBUG_HEAP=1</c> override is
/// the no-recompile equivalent). These guard the emitter shape both ways.
/// </summary>
[Collection("Runtime")]
public sealed class DebugHeapFlagTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-dbgheap-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void fsanitize_address_enables_the_checked_heap_before_the_entry()
    {
        var emitted = Compiler.EmitCSharp(
            new[] { WriteTemp("int main(void) { return 0; }") }, debugHeap: true);
        emitted.ShouldContain("Libc.EnableDebugHeap();");
        // It must precede the entry thread so it's set before main allocates.
        emitted.IndexOf("Libc.EnableDebugHeap();", System.StringComparison.Ordinal)
            .ShouldBeLessThan(emitted.IndexOf("__dotccThread.Start();", System.StringComparison.Ordinal));
    }

    [Fact]
    public void without_the_flag_the_shell_does_not_enable_the_debug_heap()
    {
        var emitted = Compiler.EmitCSharp(
            new[] { WriteTemp("int main(void) { return 0; }") });
        // The runtime block (spliced into every program) always *defines*
        // EnableDebugHeap(); what must be absent without the flag is the shell's
        // *call* to it, so malloc/free stay on the plain NativeMemory route.
        emitted.ShouldNotContain("Libc.EnableDebugHeap();");
    }
}

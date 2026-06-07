#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// The emitted program shell runs the C entry point on a thread with a large
/// stack. A native C program runs on a multi-megabyte stack (Linux defaults to
/// ~8 MB), but .NET's default is ~1 MB — too shallow for deeply-recursive C.
/// Lua's VM, for instance, guards recursion with <c>LUAI_MAXCCALLS</c> (200
/// nested C calls) on the assumption that 200 frames fit; on a 1 MB stack the
/// emitted frames overflow at ~100 and crash the runtime before Lua can raise
/// its own catchable "C stack overflow". The shell reserves 64 MB so such
/// programs reach their own recursion guards and fault gracefully.
/// </summary>
public sealed class EntryStackThreadTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-stk-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void entry_runs_on_a_large_stack_thread_and_returns_its_exit_code()
    {
        var emitted = Compiler.EmitCSharp(new[] { WriteTemp("int main(void) { return 0; }") });
        emitted.ShouldContain("new System.Threading.Thread(");
        emitted.ShouldContain("64 * 1024 * 1024");   // 64 MB stack reservation
        emitted.ShouldContain(".Join();");
        emitted.ShouldContain("return main();");      // arity-0 entry, inside the thunk
    }

    [Fact]
    public void argv_marshalling_entry_still_runs_on_the_thread()
    {
        var emitted = Compiler.EmitCSharp(
            new[] { WriteTemp("int main(int argc, char **argv) { return argc; }") });
        emitted.ShouldContain("new System.Threading.Thread(");
        emitted.ShouldContain("64 * 1024 * 1024");
        emitted.ShouldContain("return main(argc, argv);");   // argv path preserved
    }
}

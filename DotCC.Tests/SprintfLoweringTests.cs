#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>sprintf</c> / <c>snprintf</c> fluent lowering. Like
/// <c>printf</c>/<c>fprintf</c>, a variadic format call lowers to the
/// <c>SprintfBuilder</c> shape <c>sprintf(dst, fmt).Arg(…).Done()</c> (the
/// builder's bound factories only take the fixed args; the <c>%</c>-args become
/// <c>.Arg(…)</c> and <c>.Done()</c> flushes into the buffer). <c>snprintf</c>'s
/// <c>int n</c> bound takes the store-conversion cast for an unsigned/wider C
/// argument. End-to-end in the <c>sprintf-snprintf/</c> fixture.
/// </summary>
public sealed class SprintfLoweringTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-sprintf-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Emit(string body)
    {
        var src = WriteTemp(body);
        try { return Compiler.EmitCSharp(new[] { src }); }
        finally { File.Delete(src); }
    }

    [Fact]
    public void sprintf_with_varargs_lowers_to_fluent_builder()
    {
        var emitted = Emit("""
            #include <stdio.h>
            int main(void) { char buf[32]; int x = 5; sprintf(buf, "%d", x); return 0; }
            """);
        emitted.ShouldContain("sprintf(buf, Libc.L(\"%d\\0\"u8)).Arg(x).Done()");
    }

    [Fact]
    public void snprintf_with_varargs_keeps_int_literal_bound_verbatim()
    {
        var emitted = Emit("""
            #include <stdio.h>
            int main(void) { char buf[32]; int x = 5; snprintf(buf, 16, "v=%d", x); return 0; }
            """);
        emitted.ShouldContain("snprintf(buf, 16, Libc.L(\"v=%d\\0\"u8)).Arg(x).Done()");
    }

    [Fact]
    public void snprintf_unsigned_bound_is_coerced_to_int()
    {
        // C's `size_t n` reaches the factory's `int n` — an unsigned operand
        // (C# won't narrow it implicitly) takes the store-conversion cast.
        var emitted = Emit("""
            #include <stdio.h>
            int main(void) { char buf[32]; unsigned u = 10; int x = 5; snprintf(buf, u, "%d", x); return 0; }
            """);
        emitted.ShouldContain("snprintf(buf, (int)(u), Libc.L(\"%d\\0\"u8)).Arg(x).Done()");
    }

    [Fact]
    public void sprintf_without_varargs_still_calls_done()
    {
        // A SprintfBuilder left without .Done() never copies into the buffer, so
        // .Done() is appended even when there are zero %-args.
        var emitted = Emit("""
            #include <stdio.h>
            int main(void) { char buf[32]; sprintf(buf, "hi"); return 0; }
            """);
        emitted.ShouldContain("sprintf(buf, Libc.L(\"hi\\0\"u8)).Done()");
    }
}

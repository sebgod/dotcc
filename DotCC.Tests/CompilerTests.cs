#nullable enable

using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Library-level unit tests for <see cref="Compiler"/>. Each test writes a
/// short C snippet to a temp file, drives <see cref="Compiler.EmitCSharp"/>
/// in-process, and asserts on the returned C# string. No subprocesses.
/// </summary>
public sealed class CompilerTests
{
    private static string WriteTemp(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-unit-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void EmitCSharp_minimal_main_emits_unsafe_int_main()
    {
        var src = WriteTemp("""
            int main() { return 0; }
            """);
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src });

            emitted.ShouldContain("static unsafe int main()");
            emitted.ShouldContain("return 0;");
            // file-based program header so the result is `dotnet run --file`-able
            emitted.ShouldStartWith("#:property AllowUnsafeBlocks=true");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_csproj_mode_omits_file_directive()
    {
        var src = WriteTemp("int main() { return 0; }");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { src }, fileBased: false);
            emitted.ShouldNotContain("#:property");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_throws_on_parse_error()
    {
        var src = WriteTemp("int main() { return }"); // missing operand
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("parse failed");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmitCSharp_throws_when_no_main()
    {
        var src = WriteTemp("int foo() { return 0; }");
        try
        {
            Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }))
                .Message.ShouldContain("no `main`");
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void Preprocess_resolves_define_into_token_stream()
    {
        var src = WriteTemp("""
            #define ANSWER 42
            int main() { return ANSWER; }
            """);
        try
        {
            using var sw = new StringWriter();
            Compiler.Preprocess(new[] { src }, sw);
            var dumped = sw.ToString();

            // ANSWER should not appear in the output — it gets substituted by 42.
            dumped.ShouldNotContain(" ANSWER ");
            dumped.ShouldContain(" 42 ");
        }
        finally { File.Delete(src); }
    }
}

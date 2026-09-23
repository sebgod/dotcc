using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

public sealed partial class CompilerTests
{
    [Theory]
    [InlineData(".c")]
    [InlineData(".zig")]
    public void A_missing_input_file_is_a_diagnostic_not_a_crash(string extension)
    {
        // clang's shape: `clang: error: no such file or directory: 'x.c'`. It used to escape as an
        // unhandled FileNotFoundException stack trace from the frontend's File.ReadAllText.
        var missing = Path.Combine(Path.GetTempPath(), $"dotcc-missing-{Guid.NewGuid():N}{extension}");
        var ex = Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { missing }));
        ex.Message.ShouldBe($"error: no such file or directory: '{missing}'");
    }
}

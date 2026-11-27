#nullable enable

using System;
using System.IO;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// An identifier expression that no declaration in scope names (GH #343). C never had implicit
/// variables, so it is an error in every dialect, c90 included, with gcc's wording. dotcc used to
/// emit it, and the C# then failed with CS0103 (or bound it to a runtime name such as <c>true</c>).
/// </summary>
[Collection("Attributes")]
public sealed class UndeclaredIdentifierTests
{
    private static string Write(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-undeclared-{Guid.NewGuid():N}.c");
        File.WriteAllText(path, body);
        return path;
    }

    private static string Message(string body, string std = "c11")
    {
        var src = Write(body);
        try { return Should.Throw<CompileException>(() => Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std))).Message; }
        finally { File.Delete(src); }
    }

    private static string Emit(string body, string std = "c11")
    {
        var src = Write(body);
        try { return Compiler.EmitCSharp(new[] { src }, dialect: CDialect.Parse(std)); }
        finally { File.Delete(src); }
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) { n++; }
        return n;
    }

    [Theory]
    [InlineData("c90")]
    [InlineData("c99")]
    [InlineData("c11")]
    [InlineData("c17")]
    [InlineData("c23")]
    public void An_undeclared_variable_is_an_error_in_every_dialect(string std) =>
        Message("int main(void) { return undeclared_var; }\n", std)
            .ShouldContain("'undeclared_var' undeclared (first use in this function)");

    [Fact]
    public void An_undeclared_name_at_file_scope_uses_the_not_in_a_function_wording() =>
        Message("int x = missing;\nint main(void) { return 0; }\n")
            .ShouldContain("'missing' undeclared here (not in a function)");

    [Fact]
    public void A_name_is_reported_once_per_function_and_each_name_is_reported()
    {
        var msg = Message("int main(void) { int a = foo + foo; return a + bar; }\n");
        Count(msg, "'foo' undeclared").ShouldBe(1);
        Count(msg, "'bar' undeclared").ShouldBe(1);
    }

    [Fact]
    public void The_address_of_an_undeclared_function_is_an_error() =>
        Message("int main(void) { return &undeclared_fn != 0; }\n").ShouldContain("'undeclared_fn' undeclared");

    [Fact]
    public void A_parenthesized_callee_that_is_undeclared_is_an_error() =>
        // A parenthesized name is still a named callee, so it is the undeclared-function error.
        Message("int main(void) { return (g)(1); }\n").ShouldContain("implicit declaration of function 'g'");

    [Fact]
    public void Errno_needs_its_header()
    {
        Message("int main(void) { return errno; }\n").ShouldContain("'errno' undeclared");
        Emit("#include <errno.h>\nint main(void) { return errno; }\n").ShouldContain("errno");
    }

    [Fact]
    public void Pre_c23_true_needs_stdbool()
    {
        Message("int main(void) { return true; }\n", "c17").ShouldContain("'true' undeclared");
        Emit("#include <stdbool.h>\nint main(void) { return true; }\n", "c17").ShouldNotBeNull();
    }

    [Fact]
    public void The_predefined_func_identifier_needs_no_declaration() =>
        Emit("int main(void) { const char *f = __func__; return f[0] != 0; }\n").ShouldContain("main");
}

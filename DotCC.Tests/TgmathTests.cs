#nullable enable

using System.IO;
using System.Text.RegularExpressions;
using DotCC;
using Shouldly;
using Xunit;

namespace DotCC.Tests;

/// <summary>
/// Emit pins for <c>&lt;tgmath.h&gt;</c> (C11 7.25): the header's <c>_Generic</c> macros pick the
/// function at lowering time, so each call is a direct call typed from the selected prototype.
/// End-to-end in the <c>tgmath-overload</c> and <c>tgmath-complex</c> fixtures.
/// </summary>
[Collection("GenericSelection")]
public sealed class TgmathTests
{
    /// <summary>Emit <paramref name="body"/> (placed in <c>main</c>) under <c>&lt;tgmath.h&gt;</c> and
    /// return the C# of the program's own <c>main</c> function only, so the spliced runtime's own
    /// calls cannot satisfy or trip a pin.</summary>
    private static string EmitMain(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcc-tgmath-{System.Guid.NewGuid():N}.c");
        File.WriteAllText(path, "#include <tgmath.h>\nint main(void) {\n" + body + "\nreturn 0;\n}\n");
        try
        {
            var emitted = Compiler.EmitCSharp(new[] { path });
            var m = Regex.Match(emitted, @"int main\(\)\s*\{.*?\r?\n    \}\r?\n", RegexOptions.Singleline);
            m.Success.ShouldBeTrue("no main in the emit: " + emitted);
            return m.Value;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Float_argument_calls_the_f_function()
    {
        EmitMain("float x = 4.0f; float r = sqrt(x);").ShouldContain("sqrtf(");
    }

    [Fact]
    public void Integer_and_double_arguments_call_the_plain_function()
    {
        EmitMain("int i = 4; double r = sqrt(i);").ShouldContain("sqrt(");
        EmitMain("double d = 4.0; double r = sqrt(d);").ShouldNotContain("sqrtf(");
    }

    [Fact]
    public void Long_double_argument_calls_the_l_function()
    {
        EmitMain("long double x = 4.0L; long double r = sqrt(x);").ShouldContain("sqrtl(");
    }

    [Fact]
    public void Complex_argument_calls_the_c_function()
    {
        EmitMain("double _Complex z = 4.0; double _Complex r = sqrt(z);").ShouldContain("csqrt(");
        EmitMain("double _Complex z = 4.0; double _Complex r = exp(z);").ShouldContain("cexp(");
    }

    [Fact]
    public void Fabs_of_complex_is_cabs()
    {
        EmitMain("double _Complex z = 3.0; double r = fabs(z);").ShouldContain("cabs(");
    }

    [Fact]
    public void Store_of_complex_call_needs_no_conversion()
    {
        var m = EmitMain("double _Complex z = 4.0; double _Complex r = sqrt(z);");
        m.ShouldContain("csqrt(z)");
        m.ShouldNotContain("(Complex)csqrt");
    }

    [Fact]
    public void Two_argument_rule_counts_an_integer_as_double()
    {
        EmitMain("double r = pow(1.0f, 2);").ShouldNotContain("powf(");
        EmitMain("float r = pow(1.0f, 2.0f);").ShouldContain("powf(");
        EmitMain("long double r = pow(1.0L, 2);").ShouldContain("powl(");
        EmitMain("double _Complex z = 4.0; double _Complex r = pow(z, 2.0);").ShouldContain("cpow(");
    }

    [Fact]
    public void Non_generic_parameters_do_not_select()
    {
        EmitMain("int e; float r = frexp(2.0f, &e);").ShouldContain("frexpf(");
        EmitMain("float r = ldexp(1.0f, 3);").ShouldContain("ldexpf(");
        EmitMain("double r = ldexp(1.0, 3);").ShouldNotContain("ldexpf(");
        EmitMain("float r = nexttoward(1.0f, 2.0L);").ShouldContain("nexttowardf(");
        EmitMain("float r = scalbn(1.0f, 3);").ShouldContain("scalbnf(");
    }

    [Fact]
    public void Call_is_typed_from_the_selected_prototype()
    {
        EmitMain("int a = sizeof(sqrt(1.0f));").ShouldContain("int a = 4;");
        EmitMain("int a = _Generic(sqrt(1), double: 1, default: 0);").ShouldContain("int a = 1;");
        EmitMain("int a = _Generic(sqrt(1.0f), float: 1, default: 0);").ShouldContain("int a = 1;");
        EmitMain("int a = _Generic(pow(1.0f, 2), double: 1, default: 0);").ShouldContain("int a = 1;");
        EmitMain("int a = _Generic(pow(1.0f, 2.0f), float: 1, default: 0);").ShouldContain("int a = 1;");
    }

    [Fact]
    public void Long_double_outranks_double_in_either_operand_order()
    {
        EmitMain("int a = _Generic(1.0 + 1.0L, long double: 1, default: 0);").ShouldContain("int a = 1;");
        EmitMain("int a = _Generic(1.0L + 1.0, long double: 1, default: 0);").ShouldContain("int a = 1;");
    }

    [Fact]
    public void Complex_mixed_with_real_is_complex()
    {
        EmitMain("double _Complex z = 1.0; int a = _Generic(z + 1.0f, double _Complex: 1, default: 0);").ShouldContain("int a = 1;");
        EmitMain("double _Complex z = 1.0; int a = _Generic(1.0L + z, double _Complex: 1, default: 0);").ShouldContain("int a = 1;");
    }

    [Fact]
    public void Argument_with_a_side_effect_is_evaluated_once()
    {
        var m = EmitMain("int i = 0; double r = sqrt(i++);");
        Regex.Matches(m, @"i\+\+").Count.ShouldBe(1);
    }
}

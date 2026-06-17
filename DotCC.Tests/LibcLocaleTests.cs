#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;locale.h&gt;</c> shim (LocaleLib.cs). dotcc supports
/// only the "C" (== "POSIX") locale: <c>setlocale</c> selects/queries it,
/// <c>localeconv</c> reports its conventions (decimal point ".", everything else
/// empty / CHAR_MAX). End-to-end in the <c>locale-c/</c> fixture. (The
/// <c>LC_*</c> category macros live in the synthetic header — visible to emitted
/// C, not to this C# test — so the category is passed as a plain int here;
/// <c>setlocale</c> ignores it, since dotcc has one locale.)
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcLocaleTests
{
    private static string Cstr(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.ASCII.GetString(p, strlen(p));

    private static byte* B(string s)
    {
        // A tiny NUL-terminated UTF-8 buffer for passing a locale name in.
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        var p = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes.Length);
        for (int i = 0; i < bytes.Length; i++) { p[i] = bytes[i]; }
        return p;
    }

    [Fact]
    public void setlocale_C_returns_C()
    {
        Cstr(setlocale(6 /*LC_ALL*/, B("C"))).ShouldBe("C");
    }

    [Fact]
    public void setlocale_query_null_returns_C()
    {
        Cstr(setlocale(1 /*LC_NUMERIC*/, null)).ShouldBe("C");
    }

    [Fact]
    public void setlocale_empty_and_posix_return_C()
    {
        Cstr(setlocale(6, B(""))).ShouldBe("C");
        Cstr(setlocale(0 /*LC_CTYPE*/, B("POSIX"))).ShouldBe("C");
    }

    [Fact]
    public void setlocale_unsupported_returns_null()
    {
        (setlocale(6, B("xx_XX.UTF-8")) == null).ShouldBeTrue();
    }

    [Fact]
    public void localeconv_decimal_point_is_dot()
    {
        var lc = localeconv();
        Cstr(lc->decimal_point).ShouldBe(".");
        lc->decimal_point[0].ShouldBe((byte)'.');   // the lua_getlocaledecpoint value
    }

    [Fact]
    public void localeconv_other_strings_empty_and_numerics_char_max()
    {
        var lc = localeconv();
        Cstr(lc->thousands_sep).ShouldBe("");
        Cstr(lc->grouping).ShouldBe("");
        Cstr(lc->currency_symbol).ShouldBe("");
        lc->frac_digits.ShouldBe((byte)255);        // CHAR_MAX = "not available"
        lc->p_sign_posn.ShouldBe((byte)255);
    }

    [Fact]
    public void localeconv_returns_stable_pointer()
    {
        (localeconv() == localeconv()).ShouldBeTrue();  // process-lifetime static
    }
}

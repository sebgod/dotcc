#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the libc surface added for the Lua whole-program link:
/// <c>frexp</c>/<c>ldexp</c> (&lt;math.h&gt;), <c>strcoll</c> (&lt;string.h&gt;,
/// "C"-locale == <c>strcmp</c>), and <c>ungetc</c>/<c>setvbuf</c>
/// (&lt;stdio.h&gt;). End-to-end through dotcc in the
/// <c>libc-frexp-ldexp-strcoll-ungetc/</c> fixture.
/// </summary>
public sealed unsafe class LibcAddedSurfaceTests
{
    private const double Eps = 1e-12;

    [Fact]
    public void frexp_splits_into_mantissa_and_exponent()
    {
        int e;
        double m = frexp(12.0, &e);   // 12 = 0.75 * 2^4
        m.ShouldBe(0.75, Eps);
        e.ShouldBe(4);
    }

    [Fact]
    public void frexp_of_zero_returns_zero_exponent_zero()
    {
        int e;
        frexp(0.0, &e).ShouldBe(0.0);
        e.ShouldBe(0);
    }

    [Fact]
    public void frexp_float_overload_routes_to_MathF()
    {
        int e;
        float m = frexp(12.0f, &e);
        m.ShouldBe(0.75f);
        e.ShouldBe(4);
    }

    [Fact]
    public void ldexp_is_the_inverse_of_frexp()
    {
        ldexp(0.75, 4).ShouldBe(12.0, Eps);
        ldexpf(0.75f, 4).ShouldBe(12.0f);
    }

    [Fact]
    public void strcoll_matches_strcmp_in_c_locale()
    {
        strcoll(L("abc\0"u8), L("abc\0"u8)).ShouldBe(0);
        strcoll(L("abc\0"u8), L("abd\0"u8)).ShouldBeLessThan(0);
        strcoll(L("abd\0"u8), L("abc\0"u8)).ShouldBeGreaterThan(0);
    }

    // Drive ungetc through a fresh tmpfile() per test — the stdin slot is a
    // shared global whose pushback would leak across tests.
    [Fact]
    public void ungetc_pushes_one_byte_back_for_the_next_read()
    {
        FILE* f = tmpfile();
        try
        {
            fputc('A', f); fputc('B', f); rewind(f);
            int a = fgetc(f);              // 'A'
            a.ShouldBe((int)'A');
            ungetc(a, f).ShouldBe((int)'A');
            fgetc(f).ShouldBe((int)'A');   // the pushed-back byte
            fgetc(f).ShouldBe((int)'B');   // then the stream resumes
        }
        finally { fclose(f); }
    }

    [Fact]
    public void ungetc_rejects_eof_and_a_second_pushback()
    {
        FILE* f = tmpfile();
        try
        {
            fputc('Z', f); rewind(f);
            ungetc(-1, f).ShouldBe(-1);       // can't push EOF
            ungetc((int)'Q', f).ShouldBe((int)'Q');
            ungetc((int)'R', f).ShouldBe(-1); // only one char of pushback
        }
        finally { fclose(f); }
    }

    [Fact]
    public void setvbuf_is_a_validated_no_op()
    {
        setvbuf(stdout, null, 2 /*_IONBF*/, 0).ShouldBe(0);
        setvbuf(stdout, null, 99 /*invalid mode*/, 0).ShouldBe(-1);
        setvbuf(null, null, 0, 0).ShouldBe(-1);   // no such stream
    }

    [Fact]
    public void tmpnam_writes_a_nul_terminated_name_into_the_buffer()
    {
        byte* buf = stackalloc byte[260];
        var r = tmpnam(buf);
        (r == buf).ShouldBeTrue();
        strlen(buf).ShouldBeGreaterThan(0);
    }
}

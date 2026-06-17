#nullable enable

using System;
using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;stdlib.h&gt;</c> surface in
/// <see cref="DotCC.Libc.Libc"/> (StdlibLib.cs): integer conversions,
/// arithmetic, allocation extras, RNG, environment, and qsort/bsearch.
/// </summary>
[Collection("Runtime")]
public sealed unsafe class LibcStdlibTests
{
    private static string Cstr(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.ASCII.GetString(p, strlen(p));

    [Fact]
    public void atoi_parses_leading_int() => atoi(L("  42abc\0"u8)).ShouldBe(42);

    [Fact]
    public void atoi_handles_negative() => atoi(L("-7\0"u8)).ShouldBe(-7);

    [Fact]
    public void atol_parses_long() => atol(L("1234567890\0"u8)).ShouldBe(1234567890L);

    [Fact]
    public void strtol_base10() => strtol(L("100\0"u8), null, 10).ShouldBe(100L);

    [Fact]
    public void strtol_hex_autodetect() => strtol(L("0x1A\0"u8), null, 0).ShouldBe(26L);

    [Fact]
    public void strtol_octal_autodetect() => strtol(L("0777\0"u8), null, 0).ShouldBe(511L);

    [Fact]
    public void strtol_binary_base2() => strtol(L("101\0"u8), null, 2).ShouldBe(5L);

    [Fact]
    public void strtol_sets_endptr()
    {
        byte* s = L("42rest\0"u8);
        byte* end;
        strtol(s, &end, 10).ShouldBe(42L);
        Cstr(end).ShouldBe("rest");
    }

    [Fact]
    public void strtol_overflow_clamps_and_sets_errno()
    {
        errno = 0;
        strtol(L("99999999999999999999999\0"u8), null, 10).ShouldBe(long.MaxValue);
        errno.ShouldBe(ERANGE);
    }

    [Fact]
    public void strtoul_parses_unsigned() => strtoul(L("4294967295\0"u8), null, 10).ShouldBe(4294967295UL);

    [Fact]
    public void abs_labs_llabs()
    {
        abs(-5).ShouldBe(5);
        labs(-100000L).ShouldBe(100000L);
        llabs(-10000000000L).ShouldBe(10000000000L);
    }

    [Fact]
    public void div_truncates_toward_zero()
    {
        var d = div(17, 5);
        d.quot.ShouldBe(3);
        d.rem.ShouldBe(2);

        var dn = div(-17, 5);
        dn.quot.ShouldBe(-3);
        dn.rem.ShouldBe(-2);
    }

    [Fact]
    public void ldiv_and_lldiv()
    {
        var ld = ldiv(-17L, 5L);
        ld.quot.ShouldBe(-3L);
        ld.rem.ShouldBe(-2L);

        var lld = lldiv(100000000000L, 7L);
        lld.quot.ShouldBe(14285714285L);
        lld.rem.ShouldBe(5L);
    }

    [Fact]
    public void calloc_zero_initializes()
    {
        int* p = (int*)calloc(4, sizeof(int));
        try { for (int i = 0; i < 4; i++) { p[i].ShouldBe(0); } }
        finally { free(p); }
    }

    [Fact]
    public void realloc_preserves_contents()
    {
        int* p = (int*)malloc(2 * sizeof(int));
        p[0] = 11;
        p[1] = 22;
        p = (int*)realloc(p, 4 * sizeof(int));
        try
        {
            p[0].ShouldBe(11);
            p[1].ShouldBe(22);
        }
        finally { free(p); }
    }

    [Fact]
    public void rand_stays_in_range()
    {
        srand(123);
        for (int i = 0; i < 200; i++)
        {
            int r = rand();
            r.ShouldBeGreaterThanOrEqualTo(0);
            r.ShouldBeLessThanOrEqualTo(RAND_MAX);
        }
    }

    [Fact]
    public void srand_is_deterministic_per_seed()
    {
        srand(42);
        int a = rand();
        int b = rand();
        srand(42);
        rand().ShouldBe(a);
        rand().ShouldBe(b);
    }

    private static int CmpInt(void* a, void* b)
    {
        int x = *(int*)a, y = *(int*)b;
        return (x > y ? 1 : 0) - (x < y ? 1 : 0);
    }

    [Fact]
    public void qsort_sorts_ascending()
    {
        int* a = stackalloc int[6] { 5, 3, 8, 1, 9, 2 };
        qsort(a, 6, sizeof(int), &CmpInt);
        a[0].ShouldBe(1);
        a[5].ShouldBe(9);
        for (int i = 0; i < 5; i++) { a[i].ShouldBeLessThanOrEqualTo(a[i + 1]); }
    }

    [Fact]
    public void bsearch_finds_present_element()
    {
        int* a = stackalloc int[5] { 1, 3, 5, 7, 9 };
        int key = 5;
        int* hit = (int*)bsearch(&key, a, 5, sizeof(int), &CmpInt);
        (hit != null).ShouldBeTrue();
        (*hit).ShouldBe(5);
    }

    [Fact]
    public void bsearch_missing_returns_null()
    {
        int* a = stackalloc int[5] { 1, 3, 5, 7, 9 };
        int key = 4;
        ((nint)bsearch(&key, a, 5, sizeof(int), &CmpInt)).ShouldBe((nint)0);
    }

    [Fact]
    public void getenv_reads_environment()
    {
        Environment.SetEnvironmentVariable("DOTCC_TEST_VAR", "hello123");
        try { Cstr(getenv(L("DOTCC_TEST_VAR\0"u8))).ShouldBe("hello123"); }
        finally { Environment.SetEnvironmentVariable("DOTCC_TEST_VAR", null); }
    }

    [Fact]
    public void getenv_missing_returns_null() =>
        ((nint)getenv(L("DOTCC_DEFINITELY_UNSET_XYZ\0"u8))).ShouldBe((nint)0);

    [Fact]
    public void setenv_sets_and_unsetenv_removes()
    {
        try
        {
            setenv(L("DOTCC_SETENV_VAR\0"u8), L("first\0"u8), 1).ShouldBe(0);
            Cstr(getenv(L("DOTCC_SETENV_VAR\0"u8))).ShouldBe("first");
            // overwrite == 0 leaves an existing value untouched ...
            setenv(L("DOTCC_SETENV_VAR\0"u8), L("ignored\0"u8), 0).ShouldBe(0);
            Cstr(getenv(L("DOTCC_SETENV_VAR\0"u8))).ShouldBe("first");
            // ... overwrite != 0 replaces it.
            setenv(L("DOTCC_SETENV_VAR\0"u8), L("second\0"u8), 1).ShouldBe(0);
            Cstr(getenv(L("DOTCC_SETENV_VAR\0"u8))).ShouldBe("second");
            unsetenv(L("DOTCC_SETENV_VAR\0"u8)).ShouldBe(0);
            ((nint)getenv(L("DOTCC_SETENV_VAR\0"u8))).ShouldBe((nint)0);
        }
        finally { Environment.SetEnvironmentVariable("DOTCC_SETENV_VAR", null); }
    }

    [Fact]
    public void setenv_rejects_a_name_with_equals() =>
        // POSIX: name containing '=' is EINVAL.
        setenv(L("BAD=NAME\0"u8), L("x\0"u8), 1).ShouldBe(-1);

    [Fact]
    public void system_null_probes_for_a_command_processor() =>
        // C: system(NULL) returns nonzero iff a command interpreter is available.
        system(null).ShouldNotBe(0);

    [Fact]
    public void system_returns_the_child_exit_code()
    {
        // Spawns the real interpreter, so guard on platform: `cmd /c exit N`
        // (Windows) / `sh -c "exit N"` (POSIX) both yield exit status N. The
        // child's stdout goes to the inherited console — fine for a return-code
        // assertion (we don't capture it here).
        if (OperatingSystem.IsWindows())
        {
            system(L("exit 7\0"u8)).ShouldBe(7);
            system(L("rem\0"u8)).ShouldBe(0); // no-op → success
        }
        else
        {
            system(L("exit 7\0"u8)).ShouldBe(7);
            system(L("true\0"u8)).ShouldBe(0);
        }
    }
}

#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the <c>&lt;wchar.h&gt;</c> wide-string surface in
/// <see cref="DotCC.Libc.Libc"/> (WcharLib.cs) — the 16-bit siblings of the
/// <c>&lt;string.h&gt;</c> family. Read-only wide inputs come from
/// <see cref="DotCC.Libc.Libc.L16"/> (a pooled, pinned <c>char*</c> over UTF-16,
/// the string argument carrying its own trailing NUL); writable buffers come from
/// <c>stackalloc char[]</c>.
/// </summary>
public sealed unsafe class LibcWcharTests
{
    private static string Wstr(char* p) => p == null ? "<null>" : new string(p, 0, wcslen(p));

    // ---- length / compare ------------------------------------------------

    [Fact]
    public void wcslen_counts_code_units() => wcslen(L16("hello\0")).ShouldBe(5);

    [Fact]
    public void wcslen_empty_is_zero() => wcslen(L16("\0")).ShouldBe(0);

    [Fact]
    public void wcscmp_equal() => wcscmp(L16("abc\0"), L16("abc\0")).ShouldBe(0);

    [Fact]
    public void wcscmp_less() => wcscmp(L16("abc\0"), L16("abd\0")).ShouldBeLessThan(0);

    [Fact]
    public void wcscmp_greater() => wcscmp(L16("abd\0"), L16("abc\0")).ShouldBeGreaterThan(0);

    [Fact]
    public void wcsncmp_equal_within_n() => wcsncmp(L16("abcX\0"), L16("abcY\0"), 3).ShouldBe(0);

    [Fact]
    public void wcsncmp_detects_difference() => wcsncmp(L16("abc\0"), L16("abd\0"), 3).ShouldBeLessThan(0);

    [Fact]
    public void wcscoll_is_codeunit_order() => wcscoll(L16("abc\0"), L16("abd\0")).ShouldBeLessThan(0);

    // ---- copy / concat ---------------------------------------------------

    [Fact]
    public void wcscpy_copies_including_nul()
    {
        char* d = stackalloc char[8];
        wcscpy(d, L16("hi\0"));
        Wstr(d).ShouldBe("hi");
    }

    [Fact]
    public void wcsncpy_pads_with_nul_when_source_short()
    {
        char* d = stackalloc char[8];
        for (int i = 0; i < 8; i++) { d[i] = '#'; }
        wcsncpy(d, L16("hi\0"), 5);
        d[0].ShouldBe('h');
        d[1].ShouldBe('i');
        d[2].ShouldBe('\0');
        d[3].ShouldBe('\0');
        d[4].ShouldBe('\0');
        d[5].ShouldBe('#');   // untouched beyond n
    }

    [Fact]
    public void wcsncpy_writes_no_nul_when_source_fills_n()
    {
        char* d = stackalloc char[4];
        d[3] = 'Z';
        wcsncpy(d, L16("abcdef\0"), 3);
        d[0].ShouldBe('a');
        d[1].ShouldBe('b');
        d[2].ShouldBe('c');
        d[3].ShouldBe('Z');   // classic strncpy footgun: no terminating NUL
    }

    [Fact]
    public void wcscat_appends()
    {
        char* d = stackalloc char[16];
        wcscpy(d, L16("foo\0"));
        wcscat(d, L16("bar\0"));
        Wstr(d).ShouldBe("foobar");
    }

    [Fact]
    public void wcsncat_appends_bounded()
    {
        char* d = stackalloc char[16];
        wcscpy(d, L16("foo\0"));
        wcsncat(d, L16("bazXXXX\0"), 3);
        Wstr(d).ShouldBe("foobaz");
    }

    // ---- search ----------------------------------------------------------

    [Fact]
    public void wcschr_finds_first() => Wstr(wcschr(L16("a.b.c\0"), '.')).ShouldBe(".b.c");

    [Fact]
    public void wcschr_finds_nul_terminator()
    {
        char* s = L16("ab\0");
        (wcschr(s, '\0') - s).ShouldBe(2);
    }

    [Fact]
    public void wcschr_miss_returns_null() => (wcschr(L16("abc\0"), 'z') == null).ShouldBeTrue();

    [Fact]
    public void wcsrchr_finds_last() => Wstr(wcsrchr(L16("a.b.c\0"), '.')).ShouldBe(".c");

    [Fact]
    public void wcsstr_finds_substring() => Wstr(wcsstr(L16("hello world\0"), L16("world\0"))).ShouldBe("world");

    [Fact]
    public void wcsstr_empty_needle_matches_start() => Wstr(wcsstr(L16("abc\0"), L16("\0"))).ShouldBe("abc");

    [Fact]
    public void wcsstr_miss_returns_null() => (wcsstr(L16("abc\0"), L16("xy\0")) == null).ShouldBeTrue();

    [Fact]
    public void wcsspn_initial_accept_run() => wcsspn(L16("aabbc\0"), L16("ab\0")).ShouldBe(4);

    [Fact]
    public void wcscspn_initial_reject_run() => wcscspn(L16("aabbc\0"), L16("c\0")).ShouldBe(4);

    [Fact]
    public void wcspbrk_first_in_set()
    {
        char* s = L16("a.b\0");
        (wcspbrk(s, L16(".\0")) - s).ShouldBe(1);
    }

    [Fact]
    public void wcstok_tokenizes_reentrantly()
    {
        char* s = stackalloc char[8];
        wcscpy(s, L16("x,y,z\0"));
        char* save;
        Wstr(wcstok(s, L16(",\0"), &save)).ShouldBe("x");
        Wstr(wcstok(null, L16(",\0"), &save)).ShouldBe("y");
        Wstr(wcstok(null, L16(",\0"), &save)).ShouldBe("z");
        (wcstok(null, L16(",\0"), &save) == null).ShouldBeTrue();
    }

    // ---- wide memory -----------------------------------------------------

    [Fact]
    public void wmemcpy_copies_units()
    {
        char* d = stackalloc char[4];
        wmemcpy(d, L16("abcd\0"), 4);
        d[0].ShouldBe('a');
        d[3].ShouldBe('d');
    }

    [Fact]
    public void wmemmove_is_overlap_safe()
    {
        char* b = stackalloc char[6];
        wcscpy(b, L16("abcde\0"));
        wmemmove(b + 1, b, 4);   // shift "abcd" right by one
        Wstr(b).ShouldBe("aabcd");
    }

    [Fact]
    public void wmemset_fills()
    {
        char* d = stackalloc char[4];
        wmemset(d, 'x', 3);
        d[3] = '\0';
        Wstr(d).ShouldBe("xxx");
    }

    [Fact]
    public void wmemcmp_equal_and_diff()
    {
        wmemcmp(L16("xxx\0"), L16("xxx\0"), 3).ShouldBe(0);
        wmemcmp(L16("xax\0"), L16("xbx\0"), 3).ShouldBeLessThan(0);
    }

    [Fact]
    public void wmemchr_finds_in_bounds()
    {
        char* s = L16("abcd\0");
        (wmemchr(s, 'c', 4) - s).ShouldBe(2);
        (wmemchr(s, 'c', 2) == null).ShouldBeTrue();   // out of the searched span
    }

    // ---- wide -> number (transcode -> byte core) -------------------------

    [Fact]
    public void wcstol_parses_and_sets_endptr()
    {
        char* end;
        wcstol(L16("  42abc\0"), &end, 10).ShouldBe(42);
        (*end).ShouldBe('a');
    }

    [Fact]
    public void wcstol_honours_base() => wcstol(L16("ff\0"), null, 16).ShouldBe(255);

    [Fact]
    public void wcstoul_base0_detects_hex()
    {
        char* end;
        wcstoul(L16("0x1F!\0"), &end, 0).ShouldBe(31UL);
        (*end).ShouldBe('!');
    }

    [Fact]
    public void wcstoll_parses() => wcstoll(L16("-123\0"), null, 10).ShouldBe(-123);

    [Fact]
    public void wcstoull_parses() => wcstoull(L16("12345\0"), null, 10).ShouldBe(12345UL);

    [Fact]
    public void wcstod_parses_and_sets_endptr()
    {
        char* end;
        wcstod(L16("3.5xyz\0"), &end).ShouldBe(3.5);
        (*end).ShouldBe('x');
    }

    [Fact]
    public void wcstof_narrows_to_float() => wcstof(L16("1.5\0"), null).ShouldBe(1.5f);

    [Fact]
    public void wcstold_parses_as_double() => wcstold(L16("2.25\0"), null).ShouldBe(2.25);

    [Fact]
    public void wcstod_no_conversion_leaves_endptr_at_start()
    {
        char* s = L16("xyz\0");
        char* end;
        wcstod(s, &end).ShouldBe(0.0);
        (end == s).ShouldBeTrue();
    }
}

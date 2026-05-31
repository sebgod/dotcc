#nullable enable

using Shouldly;
using Xunit;
using static DotCC.Libc.Libc;

namespace DotCC.Tests;

/// <summary>
/// Unit tests for the extended <c>&lt;string.h&gt;</c> surface in
/// <see cref="DotCC.Libc.Libc"/> (StringLib.cs). Writable buffers come from
/// <c>stackalloc</c> (the <see cref="DotCC.Libc.Libc.L"/> idiom pins read-only
/// RVA data, so it can't back a mutating call).
/// </summary>
public sealed unsafe class LibcStringExtTests
{
    private static string Cstr(byte* p) =>
        p == null ? "<null>" : System.Text.Encoding.ASCII.GetString(p, strlen(p));

    [Fact]
    public void strncmp_equal_within_n() => strncmp(L("abcX\0"u8), L("abcY\0"u8), 3).ShouldBe(0);

    [Fact]
    public void strncmp_detects_difference() => strncmp(L("abc\0"u8), L("abd\0"u8), 3).ShouldBeLessThan(0);

    [Fact]
    public void strncpy_pads_with_nul_when_source_short()
    {
        byte* d = stackalloc byte[8];
        for (int i = 0; i < 8; i++) { d[i] = (byte)'#'; }
        strncpy(d, L("hi\0"u8), 5);
        d[0].ShouldBe((byte)'h');
        d[1].ShouldBe((byte)'i');
        d[2].ShouldBe((byte)0);
        d[3].ShouldBe((byte)0);
        d[4].ShouldBe((byte)0);
        d[5].ShouldBe((byte)'#');   // untouched beyond n
    }

    [Fact]
    public void strncpy_writes_no_nul_when_source_fills_n()
    {
        byte* d = stackalloc byte[4];
        d[3] = (byte)'Z';
        strncpy(d, L("abcdef\0"u8), 3);
        d[0].ShouldBe((byte)'a');
        d[1].ShouldBe((byte)'b');
        d[2].ShouldBe((byte)'c');
        d[3].ShouldBe((byte)'Z');   // classic strncpy: no terminating NUL
    }

    [Fact]
    public void strcat_appends()
    {
        byte* d = stackalloc byte[16];
        strcpy(d, L("foo\0"u8));
        strcat(d, L("bar\0"u8));
        Cstr(d).ShouldBe("foobar");
    }

    [Fact]
    public void strncat_appends_bounded()
    {
        byte* d = stackalloc byte[16];
        strcpy(d, L("foo\0"u8));
        strncat(d, L("bazXXXX\0"u8), 3);
        Cstr(d).ShouldBe("foobaz");
    }

    [Fact]
    public void strchr_finds_first() => Cstr(strchr(L("a.b.c\0"u8), '.')).ShouldBe(".b.c");

    [Fact]
    public void strchr_finds_nul_terminator()
    {
        byte* s = L("ab\0"u8);
        (strchr(s, 0) == s + 2).ShouldBeTrue();
    }

    [Fact]
    public void strchr_miss_returns_null() => ((nint)strchr(L("abc\0"u8), 'z')).ShouldBe((nint)0);

    [Fact]
    public void strrchr_finds_last() => Cstr(strrchr(L("a.b.c\0"u8), '.')).ShouldBe(".c");

    [Fact]
    public void strstr_finds() => Cstr(strstr(L("hello world\0"u8), L("wor\0"u8))).ShouldBe("world");

    [Fact]
    public void strstr_empty_needle_returns_haystack() => Cstr(strstr(L("xy\0"u8), L("\0"u8))).ShouldBe("xy");

    [Fact]
    public void strstr_miss_returns_null() => ((nint)strstr(L("abc\0"u8), L("xyz\0"u8))).ShouldBe((nint)0);

    [Fact]
    public void strspn_counts_accepted_prefix() => strspn(L("123abc\0"u8), L("0123456789\0"u8)).ShouldBe(3);

    [Fact]
    public void strcspn_counts_until_rejected() => strcspn(L("abc,def\0"u8), L(",;\0"u8)).ShouldBe(3);

    [Fact]
    public void strpbrk_finds_any() => Cstr(strpbrk(L("abc,def\0"u8), L(",;\0"u8))).ShouldBe(",def");

    [Fact]
    public void strpbrk_miss_returns_null() => ((nint)strpbrk(L("abc\0"u8), L(",;\0"u8))).ShouldBe((nint)0);

    [Fact]
    public void memcmp_equal() => memcmp(L("abc\0"u8), L("abc\0"u8), 3).ShouldBe(0);

    [Fact]
    public void memcmp_detects_difference() => memcmp(L("abc\0"u8), L("abd\0"u8), 3).ShouldBeLessThan(0);

    [Fact]
    public void memmove_handles_overlap()
    {
        byte* m = stackalloc byte[8];
        strcpy(m, L("12345\0"u8));
        memmove(m + 1, m, 4);
        Cstr(m).ShouldBe("11234");
    }

    [Fact]
    public void memchr_finds()
    {
        byte* s = L("hello\0"u8);
        (memchr(s, 'l', 5) == s + 2).ShouldBeTrue();
    }

    [Fact]
    public void memchr_miss_returns_null() => ((nint)memchr(L("hello\0"u8), 'z', 5)).ShouldBe((nint)0);

    [Fact]
    public void strtok_r_splits_into_tokens()
    {
        byte* buf = stackalloc byte[16];
        strcpy(buf, L("a,bb,ccc\0"u8));
        byte* save;
        Cstr(strtok_r(buf, L(",\0"u8), &save)).ShouldBe("a");
        Cstr(strtok_r(null, L(",\0"u8), &save)).ShouldBe("bb");
        Cstr(strtok_r(null, L(",\0"u8), &save)).ShouldBe("ccc");
        ((nint)strtok_r(null, L(",\0"u8), &save)).ShouldBe((nint)0);
    }

    [Fact]
    public void strtok_r_skips_consecutive_delimiters()
    {
        byte* buf = stackalloc byte[16];
        strcpy(buf, L("one,,two\0"u8));
        byte* save;
        Cstr(strtok_r(buf, L(",\0"u8), &save)).ShouldBe("one");
        Cstr(strtok_r(null, L(",\0"u8), &save)).ShouldBe("two");
    }

    [Fact]
    public void strtok_stateful_wrapper_works()
    {
        byte* buf = stackalloc byte[16];
        strcpy(buf, L("x:y:z\0"u8));
        Cstr(strtok(buf, L(":\0"u8))).ShouldBe("x");
        Cstr(strtok(null, L(":\0"u8))).ShouldBe("y");
        Cstr(strtok(null, L(":\0"u8))).ShouldBe("z");
    }
}

#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace DotCC.Libc;

/// <summary>
/// C99 <c>&lt;string.h&gt;</c> surface beyond the three primitives that live in
/// <see cref="Libc"/> proper (<c>strlen</c> / <c>strcmp</c> / <c>strcpy</c> and
/// the <c>mem*</c> trio <c>memset</c> / <c>memcpy</c>). Every function is a bare
/// pointer loop over NUL-terminated UTF-8 <c>byte*</c> buffers — no GC
/// allocation, matching how real C code passes strings around.
/// </summary>
/// <remarks>
/// dotcc maps C <c>size_t</c> length arguments to plain <c>int</c> (same as
/// <see cref="Libc.strlen"/> returning <c>int</c>); portable code should cast.
/// Per dotcc's reentrant-by-default rule (see C-SUPPORT.md), the tokenizer
/// primitive is <see cref="strtok_r"/> (explicit <c>char**</c> save slot); the
/// C89 stateful <see cref="strtok"/> is a thin <c>[ThreadStatic]</c> wrapper.
/// </remarks>
public static unsafe partial class Libc
{
    // ---------------------------------------------------------------------
    // Comparison / copy — bounded variants
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strncmp(a, b, n)</c> — lexicographic compare of at most
    /// <paramref name="n"/> bytes. Stops early at a mismatch or a shared NUL.
    /// </summary>
    public static int strncmp(byte* a, byte* b, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i]) { return a[i] - b[i]; }
            if (a[i] == 0) { return 0; }
        }
        return 0;
    }

    /// <summary>
    /// <c>strncpy(dst, src, n)</c> — copy up to <paramref name="n"/> bytes from
    /// <paramref name="src"/>. If <paramref name="src"/> is shorter, the
    /// remainder of <paramref name="dst"/> is NUL-padded to <paramref name="n"/>;
    /// if it is <paramref name="n"/> or longer, <em>no</em> terminating NUL is
    /// written (the classic C99 footgun, faithfully reproduced). Returns
    /// <paramref name="dst"/>.
    /// </summary>
    public static byte* strncpy(byte* dst, byte* src, int n)
    {
        int i = 0;
        for (; i < n && src[i] != 0; i++) { dst[i] = src[i]; }
        for (; i < n; i++) { dst[i] = 0; }
        return dst;
    }

    // ---------------------------------------------------------------------
    // Concatenation
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strcat(dst, src)</c> — append NUL-terminated <paramref name="src"/> to
    /// the end of NUL-terminated <paramref name="dst"/>, re-terminating. Returns
    /// <paramref name="dst"/>.
    /// </summary>
    public static byte* strcat(byte* dst, byte* src)
    {
        byte* p = dst;
        while (*p != 0) { p++; }
        while ((*p++ = *src++) != 0) { }
        return dst;
    }

    /// <summary>
    /// <c>strncat(dst, src, n)</c> — append at most <paramref name="n"/> bytes of
    /// <paramref name="src"/> to <paramref name="dst"/>, then always write a
    /// terminating NUL. Returns <paramref name="dst"/>.
    /// </summary>
    public static byte* strncat(byte* dst, byte* src, int n)
    {
        byte* p = dst;
        while (*p != 0) { p++; }
        int i = 0;
        for (; i < n && src[i] != 0; i++) { p[i] = src[i]; }
        p[i] = 0;
        return dst;
    }

    // ---------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strchr(s, c)</c> — first occurrence of the byte
    /// <c>(byte)<paramref name="c"/></c> in <paramref name="s"/>, or <c>null</c>.
    /// The terminating NUL is part of the string, so <c>strchr(s, 0)</c> returns
    /// a pointer to it (per C).
    /// </summary>
    public static byte* strchr(byte* s, int c)
    {
        byte target = (byte)c;
        while (true)
        {
            if (*s == target) { return s; }
            if (*s == 0) { return null; }
            s++;
        }
    }

    /// <summary>
    /// <c>strrchr(s, c)</c> — last occurrence of <c>(byte)<paramref name="c"/></c>
    /// in <paramref name="s"/>, or <c>null</c>. <c>strrchr(s, 0)</c> finds the
    /// terminating NUL.
    /// </summary>
    public static byte* strrchr(byte* s, int c)
    {
        byte target = (byte)c;
        byte* last = null;
        while (true)
        {
            if (*s == target) { last = s; }
            if (*s == 0) { return last; }
            s++;
        }
    }

    /// <summary>
    /// <c>strstr(haystack, needle)</c> — pointer to the first occurrence of the
    /// NUL-terminated <paramref name="needle"/> substring within
    /// <paramref name="haystack"/>, or <c>null</c>. An empty needle returns
    /// <paramref name="haystack"/> (per C).
    /// </summary>
    public static byte* strstr(byte* haystack, byte* needle)
    {
        if (*needle == 0) { return haystack; }
        for (byte* h = haystack; *h != 0; h++)
        {
            byte* a = h;
            byte* b = needle;
            while (*a != 0 && *a == *b) { a++; b++; }
            if (*b == 0) { return h; }
        }
        return null;
    }

    /// <summary>
    /// <c>strspn(s, accept)</c> — length of the initial run of
    /// <paramref name="s"/> made up only of bytes that appear in
    /// <paramref name="accept"/>.
    /// </summary>
    public static int strspn(byte* s, byte* accept)
    {
        int n = 0;
        while (s[n] != 0 && InSet(accept, s[n])) { n++; }
        return n;
    }

    /// <summary>
    /// <c>strcspn(s, reject)</c> — length of the initial run of
    /// <paramref name="s"/> made up of bytes that do <em>not</em> appear in
    /// <paramref name="reject"/>.
    /// </summary>
    public static int strcspn(byte* s, byte* reject)
    {
        int n = 0;
        while (s[n] != 0 && !InSet(reject, s[n])) { n++; }
        return n;
    }

    /// <summary>
    /// <c>strpbrk(s, accept)</c> — pointer to the first byte of
    /// <paramref name="s"/> that appears in <paramref name="accept"/>, or
    /// <c>null</c> if none do.
    /// </summary>
    public static byte* strpbrk(byte* s, byte* accept)
    {
        for (; *s != 0; s++)
        {
            if (InSet(accept, *s)) { return s; }
        }
        return null;
    }

    /// <summary>Membership test of byte <paramref name="b"/> in the
    /// NUL-terminated set <paramref name="set"/> (the NUL itself is never a
    /// member — matches <c>strspn</c>/<c>strpbrk</c> semantics).</summary>
    private static bool InSet(byte* set, byte b)
    {
        for (byte* p = set; *p != 0; p++)
        {
            if (*p == b) { return true; }
        }
        return false;
    }

    // ---------------------------------------------------------------------
    // Tokenize — reentrant primitive + stateful wrapper
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>strtok_r(str, delim, saveptr)</c> — reentrant tokenizer. On the first
    /// call pass the string in <paramref name="str"/>; on continuation calls pass
    /// <c>null</c> and the same <paramref name="saveptr"/>. Writes NULs into the
    /// source buffer to terminate each returned token (destructive, like C).
    /// Returns the next token or <c>null</c> when exhausted.
    /// </summary>
    public static byte* strtok_r(byte* str, byte* delim, byte** saveptr)
    {
        byte* p = str != null ? str : *saveptr;
        if (p == null) { return null; }

        // Skip leading delimiters.
        while (*p != 0 && InSet(delim, *p)) { p++; }
        if (*p == 0) { *saveptr = p; return null; }

        byte* tokenStart = p;
        // Advance to the next delimiter (or end).
        while (*p != 0 && !InSet(delim, *p)) { p++; }
        if (*p != 0)
        {
            *p = 0;          // terminate this token in place
            *saveptr = p + 1;
        }
        else
        {
            *saveptr = p;    // reached end of string
        }
        return tokenStart;
    }

    [ThreadStatic]
    private static byte* _strtokSave;

    /// <summary>
    /// <c>strtok(str, delim)</c> — the C89 stateful tokenizer. A thin wrapper over
    /// <see cref="strtok_r"/> with a <c>[ThreadStatic]</c> save slot, so distinct
    /// threads don't clobber each other (real C's <c>strtok</c> uses one
    /// process-global cursor — not thread-safe). New code should prefer
    /// <see cref="strtok_r"/>.
    /// </summary>
    public static byte* strtok(byte* str, byte* delim)
    {
        fixed (byte** save = &_strtokSave)
        {
            return strtok_r(str, delim, save);
        }
    }

    // ---------------------------------------------------------------------
    // Memory — compare / move / find
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>memcmp(a, b, n)</c> — compare the first <paramref name="n"/> bytes.
    /// Returns the signed difference of the first differing pair (as
    /// <c>unsigned char</c>), or 0 if all <paramref name="n"/> bytes match.
    /// </summary>
    public static int memcmp(void* a, void* b, int n)
    {
        byte* pa = (byte*)a;
        byte* pb = (byte*)b;
        for (int i = 0; i < n; i++)
        {
            if (pa[i] != pb[i]) { return pa[i] - pb[i]; }
        }
        return 0;
    }

    /// <summary>
    /// <c>memmove(dst, src, n)</c> — copy <paramref name="n"/> bytes,
    /// overlap-safe (unlike <see cref="Libc.memcpy"/>'s strict-no-overlap
    /// contract, though our memcpy is backed by the same overlap-safe primitive).
    /// Returns <paramref name="dst"/>.
    /// </summary>
    public static void* memmove(void* dst, void* src, int n)
    {
        // Buffer.MemoryCopy handles overlapping regions correctly.
        Buffer.MemoryCopy(src, dst, n, n);
        return dst;
    }

    /// <summary>
    /// <c>memchr(s, c, n)</c> — pointer to the first occurrence of
    /// <c>(byte)<paramref name="c"/></c> within the first <paramref name="n"/>
    /// bytes of <paramref name="s"/>, or <c>null</c>.
    /// </summary>
    public static void* memchr(void* s, int c, int n)
    {
        byte* p = (byte*)s;
        byte target = (byte)c;
        for (int i = 0; i < n; i++)
        {
            if (p[i] == target) { return p + i; }
        }
        return null;
    }
}

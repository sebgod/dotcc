/* mbrtowc: the wide character the next bytes of a UTF-8 string are, as a UTF-16 unit. Returns
   the bytes it took, 0 for the NUL, (size_t)-2 when the n bytes end inside a character (the
   state keeps what came so far), or (size_t)-1 with EILSEQ for an invalid sequence, an overlong
   one, a surrogate, or a character past U+FFFF, which takes two units. The wat target's libc (WatLibc), compiled with the program. */
#include <mbstate.h>
#include <errno.h>

size_t mbrtowc(wchar_t *restrict wc, const char *restrict src, size_t n, mbstate_t *restrict st)
{
    static mbstate_t internal;
    if (!st) st = &internal;
    if (!src)
    {
        /* As mbrtowc(NULL, "", 1, st): back to the initial state. */
        if (st->__opaque2) goto ilseq;
        return 0;
    }
    const unsigned char *s = (const unsigned char *)src;
    if (!n) return (size_t)-2;
    unsigned c = st->__opaque1;        /* the character's bits so far */
    unsigned need = st->__opaque2 & 3; /* the continuation bytes still to come */
    unsigned lead = st->__opaque2 >> 8;
    size_t i = 0;
    if (!need)
    {
        lead = s[0];
        if (lead < 0x80)
        {
            if (wc) *wc = (wchar_t)lead;
            return lead != 0;
        }
        if (lead < 0xc2 || lead >= 0xf0) goto ilseq;
        c = lead < 0xe0 ? lead & 0x1f : lead & 0x0f;
        need = lead < 0xe0 ? 1 : 2;
        i = 1;
    }
    for (; i < n; i++)
    {
        unsigned b = s[i];
        if ((b & 0xc0) != 0x80) goto ilseq;
        /* The second byte of a three-byte sequence: not overlong, not a surrogate. */
        if (need == 2 && ((lead == 0xe0 && b < 0xa0) || (lead == 0xed && b >= 0xa0))) goto ilseq;
        c = c << 6 | (b & 0x3f);
        if (--need == 0)
        {
            st->__opaque1 = st->__opaque2 = 0;
            if (wc) *wc = (wchar_t)c;
            return i + 1;
        }
    }
    st->__opaque1 = c;
    st->__opaque2 = need | lead << 8;
    return (size_t)-2;
ilseq:
    st->__opaque1 = st->__opaque2 = 0;
    errno = EILSEQ;
    return (size_t)-1;
}

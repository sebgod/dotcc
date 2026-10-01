/* fputwc: a wide character to a stream, as its UTF-8 bytes; it, or WEOF. A surrogate on its own
   (half a character past U+FFFF) is encoded as itself. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wint_t fputwc(wchar_t c, FILE *f)
{
    wchar_t one[2] = { c, 0 };
    char bytes[4];
    size_t n = __wcs_to_utf8(bytes, one, sizeof bytes);
    if (c == 0) { bytes[0] = 0; n = 1; }
    return fwrite(bytes, 1, n, f) == n ? (wint_t)c : WEOF;
}

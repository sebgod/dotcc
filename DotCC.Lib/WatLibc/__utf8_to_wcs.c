/* __utf8_to_wcs: UTF-8 bytes as UTF-16 units. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

size_t __utf8_to_wcs(wchar_t *dst, const char *src, size_t len, size_t max)
{
    const unsigned char *s = (const unsigned char *)src;
    size_t n = 0, i = 0;
    while (i < len)
    {
        unsigned b = s[i], c = 0xfffd;
        size_t k = b < 0x80 ? 1 : b < 0xc2 ? 0 : b < 0xe0 ? 2 : b < 0xf0 ? 3 : b < 0xf5 ? 4 : 0;
        size_t j = 1;
        if (k == 1) c = b;
        else if (k > 1 && i + k <= len)
        {
            c = b & (0x7f >> k);
            for (; j < k && (s[i + j] & 0xc0) == 0x80; j++) c = c << 6 | (s[i + j] & 0x3f);
            if (j < k || (k == 3 && c < 0x800) || (k == 4 && (c < 0x10000 || c > 0x10ffff)) || (c >= 0xd800 && c < 0xe000))
            {
                c = 0xfffd;
                if (j < k) k = j;
            }
        }
        i += k ? k : 1;
        if (c >= 0x10000)
        {
            if (n + 2 > max) break;
            if (dst) { dst[n] = (wchar_t)(0xd800 + ((c - 0x10000) >> 10)); dst[n + 1] = (wchar_t)(0xdc00 + ((c - 0x10000) & 0x3ff)); }
            n += 2;
        }
        else
        {
            if (n + 1 > max) break;
            if (dst) dst[n] = (wchar_t)c;
            n++;
        }
    }
    return n;
}

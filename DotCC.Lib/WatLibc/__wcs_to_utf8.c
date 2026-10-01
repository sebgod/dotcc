/* __wcs_to_utf8: a wide (UTF-16) string's UTF-8 bytes, whole characters only. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

size_t __wcs_to_utf8(char *dst, const wchar_t *src, size_t max)
{
    size_t n = 0;
    for (const wchar_t *p = src; *p; p++)
    {
        unsigned c = *p;
        if (c >= 0xd800 && c < 0xdc00 && p[1] >= 0xdc00 && p[1] < 0xe000)
        {
            c = 0x10000 + ((c - 0xd800) << 10) + (p[1] - 0xdc00);
        }
        size_t k = c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
        if (n + k > max) break;
        if (dst)
        {
            char *o = dst + n;
            if (k == 1) o[0] = (char)c;
            else if (k == 2) { o[0] = (char)(0xc0 | c >> 6); o[1] = (char)(0x80 | (c & 0x3f)); }
            else if (k == 3) { o[0] = (char)(0xe0 | c >> 12); o[1] = (char)(0x80 | (c >> 6 & 0x3f)); o[2] = (char)(0x80 | (c & 0x3f)); }
            else { o[0] = (char)(0xf0 | c >> 18); o[1] = (char)(0x80 | (c >> 12 & 0x3f)); o[2] = (char)(0x80 | (c >> 6 & 0x3f)); o[3] = (char)(0x80 | (c & 0x3f)); }
        }
        n += k;
        if (k == 4) p++;
    }
    return n;
}

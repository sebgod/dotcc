/* ungetwc: push a wide character back onto a stream, as its UTF-8 bytes in the read buffer's
   pushback room (musl's way); it, or WEOF. The wat target's libc (WatLibc), compiled with the program. */
#include <stdio_impl.h>
#include <wide_impl.h>
#include <string.h>

wint_t ungetwc(wint_t c, FILE *f)
{
    wchar_t one[2] = { (wchar_t)c, 0 };
    char bytes[4];
    if (c == WEOF) return WEOF;
    size_t l = c == 0 ? 1 : __wcs_to_utf8(bytes, one, sizeof bytes);
    if (c == 0) bytes[0] = 0;
    if (!f->rpos) __toread(f);
    if (!f->rpos || f->rpos < f->buf - UNGET + l) return WEOF;
    f->rpos -= l;
    memcpy(f->rpos, bytes, l);
    f->flags &= ~F_EOF;
    return c;
}

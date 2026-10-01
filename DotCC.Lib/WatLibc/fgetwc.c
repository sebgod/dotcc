/* fgetwc: the next wide character of a stream, decoded from UTF-8, or WEOF at the end or (with
   EILSEQ) on an invalid sequence or a character past U+FFFF, which one wchar_t cannot hold. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>
#include <mbstate.h>
#include <errno.h>

wint_t fgetwc(FILE *f)
{
    mbstate_t st = { 0, 0 };
    for (;;)
    {
        int c = fgetc(f);
        if (c == EOF) return WEOF;
        char b = (char)c;
        wchar_t wc;
        size_t r = mbrtowc(&wc, &b, 1, &st);
        if (r == (size_t)-1) return WEOF;
        if (r != (size_t)-2) return wc;
    }
}

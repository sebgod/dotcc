/* fgetws: at most n - 1 wide characters from a stream, through a newline, NUL-terminated; s, or
   NULL at the end with none read. The wat target's libc (WatLibc), compiled with the program. */
#include <wide_impl.h>

wchar_t *fgetws(wchar_t *s, int n, FILE *f)
{
    if (n <= 0) return NULL;
    int i = 0;
    while (i < n - 1)
    {
        wint_t c = fgetwc(f);
        if (c == WEOF)
        {
            if (i == 0) return NULL;
            break;
        }
        s[i++] = (wchar_t)c;
        if (c == '\n') break;
    }
    s[i] = 0;
    return s;
}

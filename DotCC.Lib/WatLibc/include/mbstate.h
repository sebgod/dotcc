#ifndef _MBSTATE_H
#define _MBSTATE_H

/* The multibyte conversion state and the conversions the wat libc uses (vfscanf's %lc, %ls and
   %l[), which dotcc's <wchar.h> leaves out. The multibyte encoding is UTF-8 and wchar_t a UTF-16
   unit, so a character past U+FFFF, which takes two units, is not one wchar_t: mbrtowc reports
   it as an invalid sequence. */

#include <stddef.h>
#include <wchar.h>

typedef struct {
    unsigned __opaque1, __opaque2;
} mbstate_t;

size_t mbrtowc(wchar_t *restrict wc, const char *restrict src, size_t n, mbstate_t *restrict st);
int mbsinit(const mbstate_t *st);

#endif

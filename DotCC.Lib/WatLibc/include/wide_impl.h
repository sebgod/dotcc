#ifndef _WIDE_IMPL_H
#define _WIDE_IMPL_H

/* The wat libc's wide stdio. Its streams are bytes, and its multibyte encoding is UTF-8, so a
   wide character goes out as UTF-8 and comes in from it; wchar_t is a UTF-16 unit, a character
   past U+FFFF a surrogate pair. The wide printf and scanf transcode the format (and swscanf its
   source) to UTF-8 and run the narrow ones, whose %ls, %lc and %l[ are the wide arguments. */

#include <stdio.h>
#include <stdarg.h>
#include <stddef.h>
#include <wchar.h>

/* The UTF-8 bytes of the wide string src, at most max of them and never part of a character,
   into dst (or only counted, when dst is NULL): how many. A surrogate pair is one character; a
   lone surrogate is encoded as itself. */
size_t __wcs_to_utf8(char *dst, const wchar_t *src, size_t max);

/* The wide string src as a NUL-terminated UTF-8 string from malloc, or NULL. */
char *__wcs_to_utf8_dup(const wchar_t *src);

/* The UTF-16 units of the len UTF-8 bytes at src, at most max of them, into dst (or only
   counted, when dst is NULL): how many. A byte that starts no valid character is U+FFFD. */
size_t __utf8_to_wcs(wchar_t *dst, const char *src, size_t len, size_t max);

int vfwprintf(FILE *restrict f, const wchar_t *restrict fmt, va_list ap);
int vwprintf(const wchar_t *restrict fmt, va_list ap);
int vswprintf(wchar_t *restrict s, size_t n, const wchar_t *restrict fmt, va_list ap);
int vswscanf(const wchar_t *restrict s, const wchar_t *restrict fmt, va_list ap);

#endif

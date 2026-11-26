#ifndef _STRING_H
#define _STRING_H

/* dotcc's <string.h> — string and memory operations. Implementations
   live in DotCC.Libc/Libc.cs (strlen/strcmp/strcpy + the mem* trio) and
   DotCC.Libc/StringLib.cs (everything else), spliced into every emitted
   program via the embedded-resource runtime block (see CLAUDE.md for the
   architecture). The signatures here declare the surface so the parser
   accepts `#include <string.h>` and knows the prototypes. */

#include <stddef.h>  /* size_t */

/* Length / comparison / copy. The operands only read are const, as in C, so
   a const string passes without a qualifier warning. */
int strlen(const char* s);
int strcmp(const char* a, const char* b);
int strncmp(const char* a, const char* b, size_t n);
int strcoll(const char* a, const char* b);
/* POSIX (home: <strings.h>; glibc exposes them here too, which is what
   portable code relies on — chibi calls them with only <string.h>). */
int strcasecmp(const char* a, const char* b);
int strncasecmp(const char* a, const char* b, size_t n);
char* strcpy(char* dst, const char* src);
char* strncpy(char* dst, const char* src, int n);

/* Concatenation. */
char* strcat(char* dst, const char* src);
char* strncat(char* dst, const char* src, int n);

/* Search. */
char* strchr(const char* s, int c);
char* strrchr(const char* s, int c);
char* strstr(const char* haystack, const char* needle);
int strspn(const char* s, const char* accept);
int strcspn(const char* s, const char* reject);
char* strpbrk(const char* s, const char* accept);

/* Tokenize — reentrant primitive (strtok_r) + stateful wrapper (strtok).
   Prefer strtok_r: it takes an explicit save slot, so it is thread-safe
   and re-entrant. */
char* strtok_r(char* str, const char* delim, char** saveptr);
char* strtok(char* str, const char* delim);

/* A copy of s (of at most n bytes of it, then a NUL) from malloc, or NULL (C23 7.26.2.6,
   7.26.2.7); s's length, but at most n (POSIX strnlen). */
char* strdup(const char* s);
char* strndup(const char* s, size_t n);
size_t strnlen(const char* s, size_t n);

/* Error-number -> message text (see <errno.h>). */
char* strerror(int errnum);

/* Memory. The source operands are const, as in C, so a const buffer passes
   without a qualifier warning. (The counts are still `int`, which the runtime's
   signatures take: see the runtime-fidelity rows of deferred.md.) */
void* memset(void* dst, int value, int count);
void* memcpy(void* dst, const void* src, int count);
void* memmove(void* dst, const void* src, int count);
int memcmp(const void* a, const void* b, int count);
void* memchr(const void* s, int c, int count);

#endif

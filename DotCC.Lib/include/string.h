#ifndef _STRING_H
#define _STRING_H

/* dotcc's <string.h> — string and memory operations. Implementations
   live in DotCC.Libc/Libc.cs (strlen/strcmp/strcpy + the mem* trio) and
   DotCC.Libc/StringLib.cs (everything else), spliced into every emitted
   program via the embedded-resource runtime block (see CLAUDE.md for the
   architecture). The signatures here declare the surface so the parser
   accepts `#include <string.h>` and knows the prototypes. */

#include <stddef.h>  /* size_t */

/* Length / comparison / copy. */
int strlen(char* s);
int strcmp(char* a, char* b);
int strncmp(char* a, char* b, size_t n);
int strcoll(char* a, char* b);
char* strcpy(char* dst, char* src);
char* strncpy(char* dst, char* src, int n);

/* Concatenation. */
char* strcat(char* dst, char* src);
char* strncat(char* dst, char* src, int n);

/* Search. */
char* strchr(char* s, int c);
char* strrchr(char* s, int c);
char* strstr(char* haystack, char* needle);
int strspn(char* s, char* accept);
int strcspn(char* s, char* reject);
char* strpbrk(char* s, char* accept);

/* Tokenize — reentrant primitive (strtok_r) + stateful wrapper (strtok).
   Prefer strtok_r: it takes an explicit save slot, so it is thread-safe
   and re-entrant. */
char* strtok_r(char* str, char* delim, char** saveptr);
char* strtok(char* str, char* delim);

/* Error-number -> message text (see <errno.h>). */
char* strerror(int errnum);

/* Memory. */
void* memset(void* dst, int value, int count);
void* memcpy(void* dst, void* src, int count);
void* memmove(void* dst, void* src, int count);
int memcmp(void* a, void* b, int count);
void* memchr(void* s, int c, int count);

#endif

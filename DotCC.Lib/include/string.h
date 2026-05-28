#ifndef _STRING_H
#define _STRING_H

/* dotcc's <string.h> — string and memory operations. Implementations
   live in DotCC.Libc/Libc.cs and are spliced into every emitted program
   via the embedded-resource runtime block (see CLAUDE.md for the
   architecture). The signatures here declare the surface so the parser
   accepts `#include <string.h>` and knows the prototypes.

   Note: real C uses `size_t` for length arguments. dotcc doesn't have
   `size_t` until <stdint.h> lands; using plain `int` for now matches
   the actual DotCC.Libc.Libc method signatures. When <stdint.h> ships,
   the prototypes here will switch to `size_t` and the Libc methods'
   `int` parameters will widen via implicit conversion. */

int strlen(char* s);
int strcmp(char* a, char* b);
char* strcpy(char* dst, char* src);

void* memset(void* dst, int value, int count);
void* memcpy(void* dst, void* src, int count);

#endif

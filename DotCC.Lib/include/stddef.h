#ifndef _STDDEF_H
#define _STDDEF_H

/* dotcc's <stddef.h> (C90 7.17) — common definitions: NULL, size_t, ptrdiff_t.
   (offsetof is a dotcc builtin — a grammar production, not a macro — so it
   needs no definition here.)

   NULL expands to the C# `null` keyword rather than real C's `((void*)0)`
   because C# rejects implicit `void* → T*` conversion; a bare `null` literal
   binds to any pointer type without further help.

   size_t / ptrdiff_t are 64-bit (→ C# ulong / long) per dotcc's LP64 model
   (see <stdint.h>). They live HERE — their canonical C home. The other headers
   the standard also lets expose them (<stdint.h>, <stdio.h>, <stdlib.h>,
   <string.h>, <time.h>) reach them by including this one. */

#ifndef NULL
#define NULL null
#endif

typedef unsigned long size_t;
typedef long ptrdiff_t;

#endif

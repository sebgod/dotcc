#ifndef _STDDEF_H
#define _STDDEF_H

/* dotcc's <stddef.h> — common definitions. NULL expands to the C#
   `null` keyword rather than real C's `((void*)0)` because C# rejects
   implicit `void* → T*` conversion; a bare `null` literal binds to any
   pointer type without further help. */

#define NULL null

#endif

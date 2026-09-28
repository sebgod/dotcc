#ifndef _STDDEF_H
#define _STDDEF_H

/* dotcc's <stddef.h> (C90 7.17) — common definitions: NULL, size_t, ptrdiff_t.
   (offsetof is a dotcc builtin — a grammar production, not a macro — so it
   needs no definition here.)

   NULL is real C's `((void *)0)`: the cast of a null pointer constant to
   `void *` binds to the same null-pointer node as C23 `nullptr`, which the C#
   backend spells `null` at any pointer sink (C# has no implicit `void* -> T*`).

   size_t / ptrdiff_t are 64-bit (→ C# ulong / long) per dotcc's LP64 model
   (see <stdint.h>). They live HERE — their canonical C home. The other headers
   the standard also lets expose them (<stdint.h>, <stdio.h>, <stdlib.h>,
   <string.h>, <time.h>) reach them by including this one. */

#ifndef NULL
#define NULL ((void *)0)
#endif

typedef unsigned long size_t;
typedef long ptrdiff_t;

/* unreachable() (C23 7.21.1) — the undefined-behavior marker macro. dotcc
   gives it DEFINED behavior: a loud throw (via a [DoesNotReturn] runtime
   helper), so reaching a "can't happen" point surfaces as a diagnostic rather
   than silent corruption. The prototype is always visible (an internal
   __-reserved name the user never spells directly); only the macro is C23-gated
   — pre-C23, `unreachable` stays an ordinary identifier, and the macro not
   being defined means `unreachable()` there is just an unknown call (as in a
   pre-C23 compiler without the header). Same version hygiene as <stdnoreturn.h>. */
void __dotcc_unreachable(void);

#if defined __STDC_VERSION__ && __STDC_VERSION__ >= 202311L
#define unreachable() __dotcc_unreachable()
#endif

#endif

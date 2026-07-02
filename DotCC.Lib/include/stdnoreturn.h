#ifndef _STDNORETURN_H
#define _STDNORETURN_H

/* dotcc's <stdnoreturn.h> — C11 7.23. Exposes the lowercase `noreturn` macro
   for the C11 `_Noreturn` function specifier (lowered to C#
   [System.Diagnostics.CodeAnalysis.DoesNotReturn] on the emitted method).

   C23 makes lowercase `noreturn` a first-class keyword (promoted onto the
   `_Noreturn` terminal by DialectKeywordRewriter) and deprecates this header,
   so — like <assert.h>'s static_assert and <stdalign.h>'s alignas/alignof —
   the macro is only defined for C11 <= __STDC_VERSION__ < C23: under c23 the
   keyword must not be shadowed by a macro, and pre-C11 the header defines
   nothing (`_Noreturn` itself gates as C11 under -pedantic). */

#if defined __STDC_VERSION__ && __STDC_VERSION__ >= 201112L && __STDC_VERSION__ < 202311L
#define noreturn _Noreturn
#endif

#endif

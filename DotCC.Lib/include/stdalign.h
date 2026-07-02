#ifndef _STDALIGN_H
#define _STDALIGN_H

/* dotcc's <stdalign.h> — C11 §7.15. `alignas` / `alignof` as macros for the
   `_Alignas` / `_Alignof` keywords, plus the feature-test macros the standard
   requires. C23 makes the lowercase spellings first-class keywords (promoted
   by dotcc's DialectKeywordRewriter) and removes this header's macros, so
   they are only defined for C11/C17 — same pattern as <assert.h>'s
   `static_assert`. Macro expansion runs before keyword promotion, so the two
   mechanisms compose rather than fight. */
#if defined __STDC_VERSION__ && __STDC_VERSION__ >= 201112L && __STDC_VERSION__ < 202311L
#define alignas _Alignas
#define alignof _Alignof
#define __alignas_is_defined 1
#define __alignof_is_defined 1
#endif

#endif

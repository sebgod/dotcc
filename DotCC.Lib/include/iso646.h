#ifndef _ISO646_H
#define _ISO646_H

/* dotcc's <iso646.h> (C95 7.9) — alternative spellings for the operators
   that are awkward on non-ASCII keyboards. Each is an object-like macro
   substituting the punctuation token, so `if (a and b)` preprocesses to
   `if (a && b)` before the parser ever sees it — no grammar changes. */

#define and    &&
#define and_eq &=
#define bitand &
#define bitor  |
#define compl  ~
#define not    !
#define not_eq !=
#define or     ||
#define or_eq  |=
#define xor    ^
#define xor_eq ^=

#endif

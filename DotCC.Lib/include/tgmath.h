#ifndef _TGMATH_H
#define _TGMATH_H

/* dotcc's <tgmath.h> (C99) — type-generic math. Real C uses C11
   _Generic macros so `sin(x)` dispatches to `sinf` when `x` is `float`.
   dotcc sidesteps that machinery entirely: the CMath class injected by
   Compiler.BuildShell already provides float AND double overloads for
   every math function, so `using static CMath;` gets type-generic
   dispatch for free via C# overload resolution. This header therefore
   just re-exposes <math.h> — no per-function _Generic dance required. */

#include <math.h>

#endif

#ifndef _STDBOOL_H
#define _STDBOOL_H

/* dotcc's <stdbool.h> (C99) — boolean macros. `bool` expands to the
   `_Bool` keyword (a real TypeSpec, lowered to the integer-typed CBool).
   `true`/`false` expand to the integer constants 1/0 — exactly their C
   values — which normalize through CBool when stored to a `_Bool`. (Emitting
   integers, not the C# `true`/`false` keywords, also lets a user variable
   named `true`/`false` be @-escaped: see DotCC.Libc/CBool.cs.) */

#define bool _Bool
#define true 1
#define false 0
#define __bool_true_false_are_defined 1

#endif

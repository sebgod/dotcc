#ifndef _STDBOOL_H
#define _STDBOOL_H

/* dotcc's <stdbool.h> (C99) — boolean macros. `bool` expands to the
   `_Bool` keyword (a real TypeSpec); `true` and `false` self-substitute
   so they pass through as C#-native bool literals at the Var visitor. */

#define bool _Bool
#define true true
#define false false
#define __bool_true_false_are_defined 1

#endif

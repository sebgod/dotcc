#ifndef _STDBOOL_H
#define _STDBOOL_H

/* dotcc's <stdbool.h> (C99 7.16) — boolean macros. `bool` expands to the
   `_Bool` keyword (a real TypeSpec, lowered to the integer-typed CBool).
   `true`/`false` expand to the integer constants 1/0 — exactly their C
   values — which normalize through CBool when stored to a `_Bool`. (Emitting
   integers, not the C# `true`/`false` keywords, also lets a user variable
   named `true`/`false` be @-escaped: see DotCC.Libc/CBool.cs.)

   In C23 (`__STDC_VERSION__ >= 202311L`), `bool` / `true` / `false` are
   first-class KEYWORDS and this header is vestigial — it must NOT define them
   as macros, or it would shadow the keywords and make `#ifdef bool` wrongly
   true. dotcc's DialectKeywordRewriter promotes the bare keywords under
   `-std=c23`, so the macro bodies are gated off there (the `_Bool` / `1` / `0`
   result is identical either way — this gating is for header fidelity, so e.g.
   `#ifdef bool` matches the standard). Only `__bool_true_false_are_defined`
   survives in C23 (the standard keeps it, deprecated). Pre-C23 the macros
   supply the meaning (and an undefined `__STDC_VERSION__` under `-std=c90`
   evaluates to 0 in the `#if`, so c90 gets them too). */

#if __STDC_VERSION__ < 202311L
#define bool _Bool
#define true 1
#define false 0
#endif

#define __bool_true_false_are_defined 1

#endif

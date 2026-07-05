#ifndef _SETJMP_H
#define _SETJMP_H

/* dotcc's <setjmp.h> — C99 7.13. Implemented via .NET exceptions:
   `longjmp(env, value)` throws a tagged exception and the emitter
   recognises specific setjmp shapes at the use site to wrap the
   surrounding code in a `try / catch when` block.

   Supported syntactic patterns (emitter rewrites these; other shapes
   raise CompileException):

     -- zero-vs-nonzero `if` guard (try/catch):
     if (setjmp(env))         { recovery } else { normal }
     if (setjmp(env) == 0)    { normal }   else { recovery }

     -- VALUE-CAPTURE (goto-restart: the region re-runs with r holding
        the longjmp value, so `switch`/`if` on r reaches the right arm —
        real C's "setjmp returns twice"):
     switch (setjmp(env)) { case 0: ...; case N: ...; }
     int r = setjmp(env);  switch (r) { ... }   (or: if (r == N) ...)
     r = setjmp(env);      (r = a pre-declared simple variable)

   Bonus over real C: `finally` blocks DO run during the unwind
   (.NET exception semantics) — strictly better than real longjmp's
   silent-skip-through-cleanup behaviour.

   NOT supported (rejected loudly, never lowered to the always-0 stub):
   setjmp in a loop/ternary condition, a nested sub-expression
   (`x = setjmp(env) + 1`), or a bare discarded call. */

/* jmp_buf — opaque token identifying a particular setjmp site.
   LongJmpToken is a C# class reachable through the `using static Libc;`
   in every emitted shell; it's pre-registered as a known type name
   in dotcc's TypeNameRewriter (see Compiler.PredefinedTypeNames) so
   the typedef below parses without LongJmpToken needing to be a
   keyword in dotcc's C grammar. */
typedef LongJmpToken jmp_buf;

int setjmp(jmp_buf env);
void longjmp(jmp_buf env, int value);

#endif

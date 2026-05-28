#ifndef _ASSERT_H
#define _ASSERT_H

/* dotcc's <assert.h> — C99 7.2.

   The assert macro can be redefined at every #include (the standard
   guarantees this — that's why it has no include guard wrapping the
   `assert` macro itself, only the prototype declarations). For dotcc
   the NDEBUG check happens at every include; that's the canonical
   shape and matches MSVC/glibc behavior. */

/* Re-evaluate NDEBUG every include — unlike normal include guards.
   The standard says <assert.h> must be re-includable to change the
   `assert` macro definition based on the current NDEBUG state.
   We #undef first so the new definition wins. */
#undef assert

#ifdef NDEBUG
   /* Release builds: assert is a no-op. The standard idiom is
      `((void)0)`, but dotcc's emitter doesn't translate the C
      `(void)expr` cast into a valid C# statement. Instead we route
      to a no-arg noop function — semantically identical to `(void)0`
      in statement context AND faithfully NOT evaluating `expr` (C99
      §7.2.1.1: NDEBUG disables expr evaluation entirely). Note: this
      loses comma-operator-style expression-context usage of assert
      (rare; document if a user hits it). */
   void __dotcc_assert_noop(void);
#define assert(expr) __dotcc_assert_noop()
#else
   /* Debug builds: route through DotCC.Libc.Libc.__dotcc_assert. C#
      overload resolution + [CallerArgumentExpression] at the call site
      pick the right primitive-type overload AND inline the source text
      of `expr` for the diagnostic message. Failed assertions throw an
      AssertionFailedException carrying the condition text. */
   void __dotcc_assert(int condition);
#define assert(expr) __dotcc_assert(expr)
#endif

#endif

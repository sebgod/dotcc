#ifndef _STDARG_H
#define _STDARG_H

/* dotcc's <stdarg.h> — C99 7.15. Variadic functions.

   A variadic C function `T f(fixed…, ...)` lowers to a C# method with a
   trailing `params VaArg[]` parameter; C# converts each variadic actual to a
   VaArg at the call site via implicit operators (so pointers travel too, unlike
   object[]). va_list / va_start / va_arg / va_end / va_copy are recognised by
   the emitter:

     va_list ap;                 -> VaList ap;
     va_start(ap, last);         -> ap = new VaList(_va);   (the params array)
     x = va_arg(ap, T);          -> x = ap.Next<T-accessor>();
     va_copy(dst, ap);           -> dst = ap;
     va_end(ap);                 -> ap.End();   (no-op)

   va_arg is special syntax (its second operand is a TYPE, not an expression),
   so dotcc recognises it as a builtin — exactly as clang/gcc implement it via
   __builtin_va_arg; it is not expressible as an ordinary macro. */

/* va_list — a cursor over a variadic function's argument pack. VaList is a C#
   value type reachable through `using static Libc;`; it's pre-registered as a
   known type name in dotcc's TypeNameRewriter (Compiler.PredefinedTypeNames),
   so this typedef parses without VaList being a keyword in the C grammar. */
typedef VaList va_list;

#endif

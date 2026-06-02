#ifndef _LOCALE_H
#define _LOCALE_H

/* dotcc's <locale.h> (C90 7.4) — locale control. Backed by
   DotCC.Libc/LocaleLib.cs.

   dotcc supports only the "C" (== "POSIX") locale — the one guaranteed at
   program startup and the only set of conventions portable across hosts.
   setlocale() accepts NULL (query) / "" / "C" / "POSIX" and returns "C"; any
   other locale name is reported unsupported (NULL). localeconv() reports the
   "C" locale's conventions (decimal_point ".", every other string empty, the
   numeric members CHAR_MAX). The category argument is accepted but ignored.

   `struct lconv` is the runtime Libc.lconv struct — declared ONLY there (same
   pattern as <time.h>'s struct tm): dotcc parses `struct lconv` via the usual
   `struct ID` rule and emits the bare tag `lconv`, which resolves to Libc.lconv
   through `using static Libc;`. A `struct lconv { … };` body here would make the
   emitter produce a second, colliding top-level `lconv`. (And `lconv` must NOT
   be seeded as a type name — that would break the `struct ID` parse.) Its
   members, for reference, are the C90 set: the string members `decimal_point`,
   `thousands_sep`, `grouping`, `int_curr_symbol`, `currency_symbol`,
   `mon_decimal_point`, `mon_thousands_sep`, `mon_grouping`, `positive_sign`,
   `negative_sign`, then the numeric `char` members `int_frac_digits`,
   `frac_digits`, `p_cs_precedes`, `p_sep_by_space`, `n_cs_precedes`,
   `n_sep_by_space`, `p_sign_posn`, `n_sign_posn` (plus the C99 `int_*` set). */

#ifndef NULL
#define NULL null
#endif

/* The six C-standard locale categories (7.4). Values are implementation-defined
   distinct ints (glibc's here); dotcc's setlocale ignores the category since
   there is only one locale. */
#define LC_ALL      6
#define LC_COLLATE  3
#define LC_CTYPE    0
#define LC_MONETARY 4
#define LC_NUMERIC  1
#define LC_TIME     2

char *setlocale(int category, const char *locale);
struct lconv *localeconv(void);

#endif

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

   `struct lconv` is runtime-owned (same pattern as <time.h>'s struct tm): the
   runtime supplies it as Libc.lconv, which the bare tag `lconv` resolves to
   through `using static Libc;`. The body below is for the IR only (member
   types, sizeof, offsetof); the backend emits no C# struct for it, and it must
   match Libc.lconv field for field (RuntimeOwnedAggregateTests). (And `lconv`
   must NOT be seeded as a type name — that would break the `struct ID`
   parse.) */

#ifndef NULL
#define NULL ((void *)0)
#endif

struct lconv {
    char *decimal_point;
    char *thousands_sep;
    char *grouping;
    char *int_curr_symbol;
    char *currency_symbol;
    char *mon_decimal_point;
    char *mon_thousands_sep;
    char *mon_grouping;
    char *positive_sign;
    char *negative_sign;
    char int_frac_digits;
    char frac_digits;
    char p_cs_precedes;
    char p_sep_by_space;
    char n_cs_precedes;
    char n_sep_by_space;
    char p_sign_posn;
    char n_sign_posn;
    char int_p_cs_precedes;
    char int_n_cs_precedes;
    char int_p_sep_by_space;
    char int_n_sep_by_space;
    char int_p_sign_posn;
    char int_n_sign_posn;
};

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

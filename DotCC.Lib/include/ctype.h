#ifndef _CTYPE_H
#define _CTYPE_H

/* dotcc's <ctype.h> — C99 7.4. Character classification and case
   conversion. Each function takes an `int` (the byte value or EOF)
   and returns `int` (non-zero on match, zero otherwise). The "C"
   locale only — bytes outside ASCII (0..127) return 0 from every
   predicate. Matches real C's `LC_ALL=C` behaviour. */

int isalpha(int c);
int isdigit(int c);
int isalnum(int c);
int isspace(int c);
int isupper(int c);
int islower(int c);
int isxdigit(int c);
int iscntrl(int c);
int isprint(int c);
int isgraph(int c);
int ispunct(int c);

int toupper(int c);
int tolower(int c);

#endif

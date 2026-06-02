/* <locale.h> (C90 7.4): setlocale + localeconv. dotcc supports the "C" locale
 * (the standard's program-startup default, and the only portable one):
 * setlocale(cat, "C") -> "C", localeconv()->decimal_point == ".", and the
 * locale-aware decimal point lua-style number code reads as
 * localeconv()->decimal_point[0]. gcc is the oracle (its default locale at
 * startup is also "C"). */
#include <locale.h>
#include <stdio.h>

int main(void) {
  printf("set=%s\n", setlocale(LC_ALL, "C"));        /* C */
  printf("query=%s\n", setlocale(LC_NUMERIC, NULL)); /* C (query) */

  struct lconv *lc = localeconv();
  printf("dp=%s\n", lc->decimal_point);              /* . */
  printf("dp0=%d\n", lc->decimal_point[0]);          /* 46 == '.' */
  printf("ts0=%d\n", lc->thousands_sep[0]);          /* 0 (empty) */
  return 0;
}

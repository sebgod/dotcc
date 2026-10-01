/* localeconv: the "C" locale's conventions: decimal_point ".", every other string empty,
   every numeric member CHAR_MAX (not available). The wat target's libc (WatLibc), compiled
   with the program. */
#include <locale.h>
#include <limits.h>

static struct lconv c_locale = {
    ".", "", "", "", "", "", "", "", "", "",
    CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX,
    CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX,
};

struct lconv *localeconv(void)
{
    return &c_locale;
}

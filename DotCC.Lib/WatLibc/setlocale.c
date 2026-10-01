/* setlocale: there is one locale, "C" (also called "POSIX"). A query (NULL), the native
   locale ("") and "C"/"POSIX" all answer "C"; any other name is unsupported, NULL. The
   category is accepted and ignored, as in the C# runtime (DotCC.Libc/LocaleLib.cs). The wat
   target's libc (WatLibc), compiled with the program. */
#include <locale.h>
#include <string.h>

char *setlocale(int category, const char *locale)
{
    (void)category;
    if (locale == NULL || locale[0] == 0 || strcmp(locale, "C") == 0 || strcmp(locale, "POSIX") == 0)
    {
        return "C";
    }
    return NULL;
}

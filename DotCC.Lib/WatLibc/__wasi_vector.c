/* __wasi_vector: strings whose addresses WASI wrote 4 bytes apart (args_get, environ_get) as a C
   array of pointers ending in a null pointer (see posix_impl.h). The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>
#include <posix_impl.h>

char **__wasi_vector(const unsigned *at, unsigned n)
{
    char **v = malloc((n + 1) * sizeof(char *));
    if (!v)
    {
        return 0;
    }
    for (unsigned i = 0; i < n; i++)
    {
        v[i] = (char *)(unsigned long)at[i];
    }
    v[n] = 0;
    return v;
}

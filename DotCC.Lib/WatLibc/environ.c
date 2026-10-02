/* environ: the program's environment, "NAME=value" strings ending in a null pointer, from WASI's
   environ_get; a constructor fills it before main (a program may read environ without calling
   anything first). getenv, setenv and unsetenv (musl's) keep to it. The wat target's libc (WatLibc), compiled with the program. */
#include <stdlib.h>
#include <env_impl.h>
#include <posix_impl.h>

char **environ;
static char *none[1];

[[gnu::constructor]] static void load(void)
{
    unsigned n, size;
    unsigned *at;
    char *buf;
    char **v;
    environ = none;
    if (__wasi_environ_sizes_get(&n, &size) || n == 0)
    {
        return;
    }
    at = malloc(n * sizeof(unsigned));
    buf = malloc(size);
    if (!at || !buf || __wasi_environ_get(at, buf) || !(v = __wasi_vector(at, n)))
    {
        free(at);
        free(buf);
        return;
    }
    free(at);
    environ = v;
}

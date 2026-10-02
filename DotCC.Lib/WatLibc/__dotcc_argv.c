/* __dotcc_argv: the program's arguments as main(argc, argv) takes them, from WASI's args_get, with
   argv[argc] a null pointer (C11 5.1.2.2.1). The program's entry (_start) calls it, and
   __dotcc_argc, for a main that takes them; a host that passes none gives argc 0. The wat
   target's libc (WatLibc), compiled with the program.
   dotcc-libc: also defines __dotcc_argc */
#include <stdlib.h>
#include <wasi.h>

static int argc;
static char **argv;
static char *none[1];

static void load(void)
{
    unsigned n, size;
    unsigned *at;
    char *buf;
    char **v;
    if (argv)
    {
        return;
    }
    argv = none;
    if (__wasi_args_sizes_get(&n, &size) || n == 0)
    {
        return;
    }
    /* WASI writes the arguments' addresses 4 bytes apart, as wasm32 lays a pointer out; dotcc's
       pointers take 8 bytes in memory, so argv is built from them. */
    at = malloc((n + 1) * sizeof(unsigned));
    buf = malloc(size);
    v = malloc((n + 1) * sizeof(char *));
    if (!at || !buf || !v || __wasi_args_get(at, buf))
    {
        free(at);
        free(buf);
        free(v);
        return;
    }
    for (unsigned i = 0; i < n; i++)
    {
        v[i] = (char *)(unsigned long)at[i];
    }
    v[n] = 0;
    free(at);
    argc = (int)n;
    argv = v;
}

int __dotcc_argc(void)
{
    load();
    return argc;
}

char **__dotcc_argv(void)
{
    load();
    return argv;
}

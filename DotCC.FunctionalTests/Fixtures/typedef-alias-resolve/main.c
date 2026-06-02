/* Typedef-names inside a `using` ALIAS BODY must resolve to their underlying C#
 * primitive: C# resolves a `using X = Y;` RHS IGNORING all other using-aliases,
 * so a scalar typedef-name there (size_t, intptr_t, lu_byte, …) would be an
 * unresolved-type error. Covers an alias-of-alias and a function-pointer typedef
 * whose body references another scalar typedef — plus `(void)` in a fn-ptr param
 * list meaning NO parameters (not a `void` parameter). Lua's lua.h
 * (`lua_KContext`/`lua_Reader`/`lua_Writer`) is the motivating case. */
#include <stdio.h>
#include <stddef.h>
#include <stdint.h>

typedef intptr_t my_ctx;                       /* alias-of-alias: intptr_t -> long   */
typedef int  (*reader)(size_t *sz, void *ud);  /* fn-ptr body references size_t       */
typedef void (*noargs)(void);                  /* (void) param list == no parameters  */

int main(void) {
    my_ctx c = 7;          /* the alias-of-alias resolves and is usable as an integer */
    reader r = NULL;       /* the `delegate*<ulong*, void*, int>` alias compiles       */
    noargs f = NULL;       /* the `delegate*<void>` (no-param) alias compiles          */
    printf("%d %d %d\n", (int)c, r == NULL, f == NULL);   /* 7 1 1 */
    return 0;
}

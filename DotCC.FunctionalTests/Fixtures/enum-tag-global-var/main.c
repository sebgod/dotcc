/* C keeps an `enum` TAG and an ordinary identifier in separate namespaces, so
 * `enum status { … }` and a variable `status` legally coexist. dotcc emits a
 * C# enum type `status` AND a global field `status`; an enumerator reference
 * (e.g. DONE) must resolve against the enum TYPE, not the shadowing field, and
 * a read of the variable must reach the field. (chibi's `enum sexp_opcode_names`
 * vs its `const char **sexp_opcode_names` table is the motivating case.) */
#include <stdio.h>

enum status { READY, BUSY, DONE };

const char *status = "global-var";   /* same spelling, ordinary namespace */

int main(void) {
    int s = DONE;                     /* enumerator: resolves against the enum */
    printf("%d %s\n", s, status);     /* 2 global-var */
    return 0;
}

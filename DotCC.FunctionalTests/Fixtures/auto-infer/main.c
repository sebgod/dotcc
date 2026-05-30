/* `auto` — both meanings. C23 type inference `auto x = E;` deduces x from the
 * initializer (like C# `var` / C++ `auto` / gcc's `__auto_type`); dotcc lowers
 * it straight to `var`. The pre-C23 storage-class form `auto int z` is
 * redundant (auto = the default for a block local), so dotcc drops it. gcc
 * (-std=c2x) is the oracle. */
#include <stdio.h>

int main(void) {
    auto x = 5;          /* inferred int */
    auto y = 3.14;       /* inferred double */
    auto int z = 7;      /* storage-class auto (dropped) → int z */
    auto sum = x + z;    /* inferred int */
    printf("%d %g %d %d\n", x, y, z, sum);
    return 0;
}

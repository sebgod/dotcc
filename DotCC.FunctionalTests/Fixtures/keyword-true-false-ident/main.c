/* Pre-C23, with no <stdbool.h>, `true` and `false` are ordinary identifiers
 * (legal C). They are also C# keywords, so dotcc @-escapes them (@true/@false).
 * This only became possible once `true`/`false` stopped being emitted as C#
 * literals (they now lower to 1/0 via the macro / c23 paths), so the spelling
 * reaches the emitter as an identifier only when it really is one. gcc accepts
 * `true`/`false` as identifiers here (no stdbool included). */
#include <stdio.h>

int main(void) {
    int true = 5;
    int false = 3;
    printf("%d\n", true + false);   /* 8 */
    return 0;
}

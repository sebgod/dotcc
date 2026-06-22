/* Milestone T — the unified compile-time interpreter folds relational, logical,
 * and ternary operators in constant-expression positions. dotcc's older
 * integer-only constant folder evaluated literals, unary, and arithmetic/bitwise
 * binary operators, but returned "non-constant" for `>`, `||`, and `?:`. The
 * shared ComptimeInterpreter evaluates the whole side-effect-free value subset,
 * so an enum value (and any constant-expression position) may use them. gcc and
 * MSVC fold all of these too — this fixture is differential-clean. */
#include <stdio.h>

enum E {
    GT   = (3 > 2),          /* relational  -> 1 */
    NE   = (4 != 4) + 10,    /* relational  -> 0 + 10 = 10 */
    LOG  = (0 || 2) * 3,     /* logical-or  -> 1 * 3 = 3 */
    TERN = (GT ? 7 : 9),     /* ternary, references the earlier member -> 7 */
};

int main(void) {
    /* A constant-expression array bound that needs the same folding. */
    int arr[(2 > 1) ? 4 : 2];          /* size 4 */
    printf("%d %d %d %d %zu\n",
           GT, NE, LOG, TERN, sizeof(arr) / sizeof(arr[0]));
    return 0;
}

/* An EXTERNAL `helper` (external linkage) with a different signature and body
 * than a_static_helper.c's file-local `static helper`. It owns the canonical
 * name; the static one must not collide with it, and each TU's calls must bind
 * to the right body. */
#include <stdio.h>

int helper(int a, int b) { return a * b; }      /* extern: multiply */

extern int use_static(void);                     /* defined in a_static_helper.c */

int main(void) {
    /* use_static() reaches the static helper (10+1=11); helper(6,7) is this
     * TU's external one (6*7=42). */
    printf("%d %d\n", use_static(), helper(6, 7));
    return 0;
}

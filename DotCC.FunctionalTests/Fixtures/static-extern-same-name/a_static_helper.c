/* A FILE-LOCAL `static` helper named `helper` — internal linkage, visible only
 * inside THIS translation unit. The other TU (b_main.c) defines a DIFFERENT,
 * EXTERNAL function also called `helper`; C keeps the two distinct (the tag/
 * ordinary-linkage rules). In dotcc's whole-program merge the external one owns
 * the canonical name and this internal-linkage one is renamed out of the way —
 * yet `use_static` must still call THIS body. */
static int helper(int x) { return x + 1; }     /* private: increment */

int use_static(void) { return helper(10); }    /* calls the static helper → 11 */

/* printf %o — unsigned octal, plus the `#` flag (leading 0) and the PRIo32
   macro from <inttypes.h>. All values stay 32-bit so MSVC (LLP64) agrees;
   octal output is standard and deterministic, so both oracles validate. */
#include <stdio.h>
#include <inttypes.h>

int main(void) {
    printf("%o %o %o\n", 8, 64, 0);      /* 10 100 0 */
    printf("%#o %#o %#o\n", 8, 511, 0);  /* 010 0777 0 */
    printf("%o\n", 255);                 /* 377 */

    uint32_t x = 64;
    printf("PRIo32=%" PRIo32 "\n", x);   /* PRIo32=100 */
    return 0;
}

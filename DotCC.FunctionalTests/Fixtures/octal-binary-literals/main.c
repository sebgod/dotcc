/* Octal (`0`-prefix, C89) and binary (`0b`, C23) integer literals. C# has
 * NEITHER spelling the C way — a leading `0` is plain decimal in C#, so `0755`
 * there would mean 755, not 493 — so dotcc converts an octal constant to its
 * value and passes a binary one through (C# does have `0b`). gcc (-std=c2x) is
 * the oracle. */
#include <stdio.h>

int main(void) {
    int perm  = 0644;       /* rw-r--r-- -> 420 */
    int mode  = 0755;       /* rwxr-xr-x -> 493 */
    int flags = 0b1011;     /* binary    -> 11  */
    long big  = 0777777L;   /* octal long -> 262143 */
    printf("perm=%d mode=%d flags=%d big=%ld\n", perm, mode, flags, big);
    printf("0=%d 010=%d 0x10=%d 0b10=%d\n", 0, 010, 0x10, 0b10);
    return 0;
}

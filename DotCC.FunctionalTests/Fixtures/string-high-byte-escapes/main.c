#include <stdio.h>

/* String literals with high-byte escapes (> 0x7F). A C# u8 literal can't
   carry these (it UTF-8-encodes \x80+ into two bytes), so dotcc emits the
   string as a constant byte-array (RVA-backed) preserving the exact C bytes.
   Mixed hex/octal escapes, escapes adjacent to ASCII, and a pure-ASCII
   string (which still uses the readable u8 literal) are all exercised. */

int main(void) {
    const char *hex = "\xff\xfe\x80";   /* three high bytes */
    const char *oct = "\377A";          /* octal 255, then 'A' (0x41) */
    const char *mix = "A\xc3Z";         /* ASCII, high byte, ASCII */
    const char *asc = "hi";             /* stays a u8 literal */

    printf("%d %d %d\n", (unsigned char)hex[0], (unsigned char)hex[1], (unsigned char)hex[2]);
    printf("%d %d\n", (unsigned char)oct[0], (unsigned char)oct[1]);
    printf("%d %d %d\n", (unsigned char)mix[0], (unsigned char)mix[1], (unsigned char)mix[2]);
    printf("%s\n", asc);
    return 0;
}

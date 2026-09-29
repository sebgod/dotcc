#include <stdio.h>
#include <string.h>
#include <unistd.h>

/* Non-ASCII text on the console through every byte path: putchar a byte at a
 * time, fwrite and write(1, ...) with a UTF-8 sequence split across two calls
 * (CPython writes all of its stdout through write), and fputs. dotcc's console
 * is a text writer, so each stream assembles the UTF-8 it is given; a native
 * program writes the same bytes raw. */

int main(void) {
    const char *s = "\xc3\xa9t\xc3\xa9 \xf0\x9f\x90\x8d";   /* "été 🐍" */
    for (const char *p = s; *p; p++) {
        putchar((unsigned char)*p);
    }
    putchar('\n');

    fwrite(s, 1, 1, stdout);                   /* the first byte of é alone */
    fwrite(s + 1, 1, strlen(s) - 1, stdout);   /* then the rest */
    fputs("\n", stdout);
    fflush(stdout);

    write(1, "\xce\xa9", 1);                   /* Ω split across two writes */
    write(1, "\xa9 ok\n", 5);

    fputs("\xe2\x82", stdout);                 /* € split across two fputs */
    fputs("\xac euro\n", stdout);
    return 0;
}

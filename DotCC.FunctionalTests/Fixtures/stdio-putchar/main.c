/* End-to-end coverage for <stdio.h> character output: putchar / putc / fputc.
   (getchar/fgetc/fgets read stdin, which the fixture harness doesn't feed, so
   they are unit-tested with a StringReader instead.) ISO C — both oracles
   validate. */

#include <stdio.h>

int main(void)
{
    /* putchar returns the byte written. */
    for (char c = 'A'; c <= 'E'; c++)
        putchar(c);
    putchar('\n');

    /* putc / fputc to stdout. */
    putc('h', stdout);
    fputc('i', stdout);
    putchar('\n');

    /* putchar's return value. */
    int r = putchar('Z');
    printf("\nreturned %d\n", r);

    return 0;
}

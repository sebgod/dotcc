/* getrandom() / getentropy() (<sys/random.h>): the counts, the 256-byte
   getentropy limit, and bytes that are not all zero (a 2^-256 chance). */
#include <errno.h>
#include <stdio.h>
#include <sys/random.h>
#include <unistd.h>

static int any_set(const unsigned char *p, int n)
{
    int i;
    for (i = 0; i < n; i++) {
        if (p[i] != 0) {
            return 1;
        }
    }
    return 0;
}

int main(void)
{
    unsigned char buf[300] = {0};
    long n = getrandom(buf, 32, 0);
    printf("getrandom: %ld %d\n", n, any_set(buf, 32));
    n = getrandom(buf, 32, GRND_NONBLOCK);
    printf("nonblock: %ld\n", n);
    unsigned char more[32] = {0};
    int r = getentropy(more, sizeof more);
    printf("getentropy: %d %d\n", r, any_set(more, 32));
    errno = 0;
    r = getentropy(buf, 257);
    printf("too long: %d %d\n", r, errno == EIO);
    return 0;
}

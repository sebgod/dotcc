/* End-to-end coverage for <errno.h> + strerror. dotcc's strerror messages are
   modeled on glibc, so the gcc (WSL/glibc) oracle matches byte-for-byte; MSVC's
   CRT uses different wording ("Result too large" vs "Numerical result out of
   range"), so this fixture opts out of the MSVC oracle (no-msvc-oracle.txt).
   errno's E* values (EDOM=33, ERANGE=34) match across glibc and MSVC. */

#include <stdio.h>
#include <string.h>
#include <errno.h>

int main(void)
{
    errno = 0;
    printf("errno start = %d\n", errno);

    errno = ERANGE;
    printf("ERANGE = %d: %s\n", errno, strerror(errno));

    errno = EDOM;
    printf("EDOM = %d: %s\n", errno, strerror(errno));

    printf("EINVAL: %s\n", strerror(EINVAL));
    printf("ENOENT: %s\n", strerror(ENOENT));
    printf("strerror(0): %s\n", strerror(0));

    return 0;
}

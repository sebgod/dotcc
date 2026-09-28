#ifndef _SYS_RANDOM_H
#define _SYS_RANDOM_H

/* dotcc's <sys/random.h> (glibc): random bytes from the host's
   cryptographically secure generator (System.Security.Cryptography's
   RandomNumberGenerator, the OS entropy source), which never blocks and is
   seeded from boot, so every flag behaves as GRND_NONBLOCK would once the
   pool is ready. getentropy() fills at most 256 bytes, as POSIX has it. */

#include <stddef.h>      /* size_t */
#include <sys/types.h>   /* ssize_t */

#define GRND_NONBLOCK 0x01
#define GRND_RANDOM   0x02
#define GRND_INSECURE 0x04

ssize_t getrandom(void *buf, size_t buflen, unsigned int flags);
int getentropy(void *buffer, size_t length);

#endif /* _SYS_RANDOM_H */

// dotcc advertises the POSIX.1-2008 surface it provides on every target OS by
// defining _POSIX_VERSION in <unistd.h> (like a real system). So portable code
// that gates its POSIX path on `#ifdef _POSIX_VERSION` takes that branch on
// dotcc and the gated calls (kill/getpid here) actually run — instead of
// silently compiling the non-POSIX fallback. _POSIX_C_SOURCE is set so real
// glibc also exposes _POSIX_VERSION under a strict -std= (dotcc ignores it: its
// headers declare unconditionally); MSVC has no <unistd.h>/kill, so the MSVC
// oracle opts out. We assert the branch is taken + the call works, NOT the
// version's numeric value (that's glibc-release-specific).
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <unistd.h>
#include <signal.h>

int main(void) {
#if defined(_POSIX_VERSION)
    printf("posix kill=%d\n", kill(getpid(), 0));   // POSIX branch taken; call works
#else
    printf("no posix\n");
#endif
    return 0;
}

// kill(pid, 0) is the portable "does this process exist / may I signal it?"
// probe — it delivers NO signal. dotcc forwards kill to the real OS (POSIX
// kill(2) / Windows OpenProcess), so kill(getpid(), 0) succeeds (0) and a kill
// of an impossible pid fails with -1 / ESRCH on every platform. MSVC's CRT has
// no <signal.h> kill() (nor <unistd.h>), so the MSVC oracle opts out; gcc on
// Linux (x64 + arm64) differential-tests it.
//
// kill/getpid are POSIX, not ISO C — glibc hides their prototypes under a strict
// -std= unless a feature-test macro is set, so declare the POSIX surface (dotcc
// ignores it: its synthetic <signal.h>/<unistd.h> declare them unconditionally).
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <signal.h>     // kill
#include <unistd.h>     // getpid
#include <errno.h>      // ESRCH

int main(void) {
    int self = kill(getpid(), 0);     // we exist and may signal ourselves -> 0
    errno = 0;
    int gone = kill(2147483647, 0);   // above any OS pid_max -> -1, ESRCH
    printf("%d %d %d\n", self, gone, errno == ESRCH);
    return 0;
}

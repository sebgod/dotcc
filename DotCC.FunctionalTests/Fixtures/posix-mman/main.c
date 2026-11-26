/* <sys/mman.h> anonymous mappings, sigaltstack, flockfile, _setjmp,
   snprintf(NULL, 0, ...) and GCC's __atomic builtins: the POSIX and GNU
   surface Goose's runtime takes on its generic POSIX path. */
#define _DEFAULT_SOURCE 1 /* glibc: the POSIX and BSD names a strict -std hides */
#include <stdio.h>
#include <string.h>
#include <setjmp.h>
#include <signal.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>

static jmp_buf env;

static void fail_later(void) { _longjmp(env, 7); }

int main(void) {
    size_t page = (size_t)sysconf(_SC_PAGESIZE);
    size_t len = 16 * page;
    unsigned char *p = mmap(NULL, len, PROT_READ | PROT_WRITE,
                            MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    if (p == MAP_FAILED) { printf("mmap failed\n"); return 1; }
    printf("zeroed %d\n", p[0] == 0 && p[len - 1] == 0);
    memset(p, 0xAB, len);
    printf("guard %d\n", mprotect(p + len - page, page, PROT_NONE));
    printf("discard %d\n", madvise(p, 2 * page, MADV_DONTNEED));
    printf("after discard %d %d\n", p[0], p[2 * page]);
    printf("unmap %d\n", munmap(p, len));
    printf("file mapping fails %d\n", mmap(NULL, page, PROT_READ, MAP_PRIVATE, 99, 0) == MAP_FAILED);

    stack_t ss, old;
    printf("altstack before %d\n", sigaltstack(NULL, &old) == 0 && (old.ss_flags & SS_DISABLE) != 0);
    ss.ss_size = SIGSTKSZ;
    ss.ss_sp = malloc(ss.ss_size);
    ss.ss_flags = 0;
    printf("altstack set %d\n", sigaltstack(&ss, NULL));
    printf("altstack back %d\n", sigaltstack(NULL, &old) == 0 && old.ss_sp == ss.ss_sp && old.ss_flags == 0);
    ss.ss_size = 16;
    printf("altstack too small %d\n", sigaltstack(&ss, NULL) == -1);
    ss.ss_flags = SS_DISABLE;
    sigaltstack(&ss, NULL);
    free(old.ss_sp);

    flockfile(stdout);
    printf("locked twice %d\n", ftrylockfile(stdout));
    funlockfile(stdout);
    funlockfile(stdout);

    if (_setjmp(env)) {
        printf("jumped\n");
    } else {
        fail_later();
    }

    int n = snprintf(NULL, 0, "%s:%d", "abc", 1234);
    char buf[16];
    snprintf(buf, sizeof buf, "%s:%d", "abc", 1234);
    printf("measure %d %s\n", n, buf);

    static int counter = 40;
    int before = __atomic_fetch_add(&counter, 1, __ATOMIC_SEQ_CST);
    int after = __atomic_add_fetch(&counter, 1, __ATOMIC_SEQ_CST);
    __atomic_store_n(&counter, 100, __ATOMIC_RELAXED);
    int expected = 100;
    int swapped = __atomic_compare_exchange_n(&counter, &expected, 5, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    printf("atomics %d %d %d %d %d\n", before, after, swapped, __atomic_load_n(&counter, __ATOMIC_ACQUIRE),
           __atomic_always_lock_free(sizeof(int), 0));
    return 0;
}

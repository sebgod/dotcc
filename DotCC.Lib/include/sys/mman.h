#ifndef _SYS_MMAN_H
#define _SYS_MMAN_H

/* dotcc's <sys/mman.h>: POSIX memory mappings, anonymous ones in full
   (DotCC.Libc's MmanLib). The constants are Linux's, as everywhere in dotcc's
   headers; the runtime translates them where the host differs.

   - Linux and macOS: mmap / munmap / mprotect / madvise / msync forward to the
     host's own, so a MAP_NORESERVE reservation, a PROT_NONE guard page and
     MADV_DONTNEED behave exactly as on that system.
   - Windows: an anonymous mapping is VirtualAlloc'd. Windows has no overcommit,
     so a readable or writable mapping is committed in full when it is made (a
     PROT_NONE one is only reserved); where the commit limit refuses it, mmap
     fails ENOMEM, as Linux's strict accounting does. mprotect changes the
     protection (committing a reserved range made accessible), MADV_DONTNEED
     gives back the pages and leaves them zero as Linux does, and munmap of a
     whole mapping releases it (part of one is decommitted, its address range
     staying reserved).
   - A mapping of a file (no MAP_ANONYMOUS) takes one of dotcc's descriptors,
     which on Linux and macOS is translated to the host's descriptor of that
     file; one that names no open file fails EBADF everywhere. Windows has no
     file mappings here (ENODEV): dotcc's descriptors are no OS handles. */

#include <sys/types.h>

#define PROT_NONE  0
#define PROT_READ  1
#define PROT_WRITE 2
#define PROT_EXEC  4

#define MAP_SHARED    0x01
#define MAP_PRIVATE   0x02
#define MAP_FIXED     0x10
#define MAP_ANONYMOUS 0x20
#define MAP_ANON      MAP_ANONYMOUS
#define MAP_NORESERVE 0x4000

#define MAP_FAILED ((void *)-1)

/* madvise advice; only MADV_DONTNEED changes what a program can observe. */
#define MADV_NORMAL     0
#define MADV_RANDOM     1
#define MADV_SEQUENTIAL 2
#define MADV_WILLNEED   3
#define MADV_DONTNEED   4
#define MADV_FREE       8
#define MADV_HUGEPAGE   14
#define MADV_NOHUGEPAGE 15

/* msync flags. */
#define MS_ASYNC      1
#define MS_INVALIDATE 2
#define MS_SYNC       4

void *mmap(void *addr, size_t length, int prot, int flags, int fd, off_t offset);
int munmap(void *addr, size_t length);
int mprotect(void *addr, size_t length, int prot);
int madvise(void *addr, size_t length, int advice);
int msync(void *addr, size_t length, int flags);

#endif /* _SYS_MMAN_H */

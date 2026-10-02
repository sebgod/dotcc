#ifndef _DOTCC_POSIX_IMPL_H
#define _DOTCC_POSIX_IMPL_H

/* What the wat libc's POSIX layer over WASI shares. WASI names a file by a path relative to a
   directory descriptor, the host's preopened directories at the start; the layer keeps the
   current directory itself (WASI has none), so an absolute path, or one relative to the current
   directory, is resolved to a preopen and a path under it. */

#include <wasi.h>
#include <sys/stat.h>

/* The longest path the layer resolves, its NUL included. */
#define __DOTCC_PATH_MAX 4096

/* The preopened directory path is under, as its descriptor, with the rest of the path relative
   to it in rel (size bytes; "." for the directory itself). The path is made absolute against
   the current directory, its "." and ".." taken out by name, and matched against the preopens'
   names, the longest match winning. -1 with errno set (ENOENT: path is empty, or no preopen
   holds it; ENAMETOOLONG). */
int __wasi_resolve(const char *path, char *rel, unsigned long size);

/* The struct stat for a WASI filestat. WASI has no permissions, so the mode's are a directory's
   0755, a symbolic link's 0777 and any other file's 0644. */
void __wasi_to_stat(const struct __wasi_filestat *fs, struct stat *st);

/* Set errno for the WASI errno err and return -1: how a call that failed ends. */
int __wasi_fail(int err);

/* The n strings whose addresses WASI wrote at at (4 bytes apart, as wasm32 lays a pointer out)
   as a C array of them (dotcc's pointers take 8 bytes in memory) that ends in a null pointer,
   from malloc; NULL when there is no memory. */
char **__wasi_vector(const unsigned *at, unsigned n);

/* Block for ns nanoseconds, through poll_oneoff on the monotonic clock: 0, or WASI's errno. */
int __wasi_sleep(unsigned long ns);

#endif

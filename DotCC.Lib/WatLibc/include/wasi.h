#ifndef _DOTCC_WASI_H
#define _DOTCC_WASI_H

/* The WASI preview1 functions the wat target's libc calls. A function named __wasi_<name>
   that nothing defines is the module's import of wasi_snapshot_preview1.<name>, typed by its
   prototype here: a pointer is the i32 address it is on the stack, a 64-bit integer an i64,
   as WASI's ABI has them. Each returns WASI's errno, 0 for success. */

#define __WASI_CLOCKID_REALTIME           0
#define __WASI_CLOCKID_MONOTONIC          1
#define __WASI_CLOCKID_PROCESS_CPUTIME_ID 2
#define __WASI_CLOCKID_THREAD_CPUTIME_ID  3

/* A buffer as WASI's I/O takes it: its address and its length, two 32-bit fields as wasm32 lays
   them out (dotcc's own pointers are 8 bytes in memory, so this is not a pointer and a size_t). */
struct __wasi_iovec
{
    unsigned buf;
    unsigned buf_len;
};
#define __wasi_addr(p) ((unsigned)(unsigned long)(p))

/* Write the iovs_len buffers at iovs to fd, in order; the count written into *nwritten. */
int __wasi_fd_write(int fd, const struct __wasi_iovec *iovs, int iovs_len, unsigned *nwritten);

/* Read from fd into the iovs_len buffers at iovs, in order; the count read (0 at the end) into
   *nread. */
int __wasi_fd_read(int fd, const struct __wasi_iovec *iovs, int iovs_len, unsigned *nread);

/* Move fd's offset (whence: 0 from the start, 1 from where it is, 2 from the end, as SEEK_SET,
   SEEK_CUR and SEEK_END are); the new offset into *newoffset. */
int __wasi_fd_seek(int fd, long offset, int whence, unsigned long *newoffset);

/* Close fd. */
int __wasi_fd_close(int fd);

/* The C errno for a WASI errno. */
int __wasi_errno(int err);

/* The time of a clock, in nanoseconds, into *time; precision is a hint. */
int __wasi_clock_time_get(unsigned clock_id, unsigned long precision, unsigned long *time);

/* buf_len random bytes from the host's secure source into buf (a WASI size is 32 bits). */
int __wasi_random_get(void *buf, unsigned buf_len);

/* wasi-threads (imported from module "wasi" as "thread-spawn"): run wasi_thread_start(tid, arg)
   in a new thread's instance of the module; the new thread's id, or a negative error. */
int __wasi_thread_spawn(void *start_arg);

#endif

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

/* The time of a clock, in nanoseconds, into *time; precision is a hint. */
int __wasi_clock_time_get(unsigned clock_id, unsigned long precision, unsigned long *time);

/* buf_len random bytes from the host's secure source into buf (a WASI size is 32 bits). */
int __wasi_random_get(void *buf, unsigned buf_len);

#endif

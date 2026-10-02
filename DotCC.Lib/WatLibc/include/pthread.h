#ifndef _DOTCC_WATLIBC_PTHREAD_H
#define _DOTCC_WATLIBC_PTHREAD_H

/* Nothing: dlmalloc includes <pthread.h> whenever its locks are not gcc's spin locks, and the
   wat libc's are its own (C11 atomics, see dlmalloc/malloc.c). The wat target's threads are
   C11's <threads.h>. */

#endif

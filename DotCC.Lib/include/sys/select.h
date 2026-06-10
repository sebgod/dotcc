#ifndef _SYS_SELECT_H
#define _SYS_SELECT_H

/* dotcc's <sys/select.h> — POSIX puts the select() surface here; dotcc's
   single definition lives in <unistd.h> (where glibc also exposes it
   transitively, which is what portable code actually relies on). */

#include <unistd.h>

#endif /* _SYS_SELECT_H */

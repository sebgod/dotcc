#ifndef _SYS_TYPES_H
#define _SYS_TYPES_H

/* dotcc's <sys/types.h> — the POSIX base typedefs portable code spells.
   LP64 widths, matching dotcc's data model. off_t shares its guard with
   <stdio.h> (either may arrive first). */

#ifndef _OFF_T_DEFINED
#define _OFF_T_DEFINED
typedef long off_t;
#endif

/* size_t / time_t are owned by <stddef.h> / <time.h> (same spellings);
   include those rather than redefining here. */
#include <stddef.h>

typedef long  ssize_t;
typedef int   pid_t;
typedef unsigned int mode_t;
typedef unsigned int uid_t;
typedef unsigned int gid_t;

#endif /* _SYS_TYPES_H */

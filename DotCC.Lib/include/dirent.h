#ifndef _DIRENT_H
#define _DIRENT_H

/* dotcc's <dirent.h> — directory iteration backed by .NET enumeration
   (DotCC.Libc.PosixFsLib: opendir/readdir/closedir/rewinddir). Enough surface
   for chibi-scheme's (chibi filesystem) directory-fold.

   LAYOUT NOTE: d_name is placed FIRST (offset 0) deliberately. It is the only
   field portable C reads, and the runtime fills it by a fixed offset (like
   <sys/stat.h>'s struct stat); putting it first makes that offset 0 — robust
   against any trailing-field padding. readdir() returns "." and ".." first,
   then the real entries, exactly as a POSIX readdir does. */

/* DIR is opaque to portable C — only ever held as a DIR* token from opendir().
   It must be a COMPLETE type so dotcc emits a real C# struct for the `(DIR*)`
   casts; the single dummy field is never read (the runtime keys its state table
   by the token's address). */
typedef struct { void *__handle; } DIR;

struct dirent {
    char           d_name[256];   /* offset 0 — the entry name (see note) */
    unsigned long  d_ino;         /* 0: .NET does not report inode numbers */
    unsigned char  d_type;        /* a DT_* value below */
};

/* d_type values (Linux's). readdir reports a directory, a regular file or a
   symbolic link, as .NET's enumeration tells them apart (anything else, a
   device or a socket, reads as a regular file); "." and ".." are DT_DIR. */
#define DT_UNKNOWN 0
#define DT_FIFO    1
#define DT_CHR     2
#define DT_DIR     4
#define DT_BLK     6
#define DT_REG     8
#define DT_LNK     10
#define DT_SOCK    12

DIR *opendir(const char *name);
struct dirent *readdir(DIR *dirp);
int closedir(DIR *dirp);
void rewinddir(DIR *dirp);

#endif /* _DIRENT_H */

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
    unsigned long  d_ino;
    unsigned char  d_type;
};

DIR *opendir(const char *name);
struct dirent *readdir(DIR *dirp);
int closedir(DIR *dirp);
void rewinddir(DIR *dirp);

#endif /* _DIRENT_H */

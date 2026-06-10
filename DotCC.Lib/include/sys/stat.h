#ifndef _SYS_STAT_H
#define _SYS_STAT_H

/* dotcc's <sys/stat.h> — enough surface for the portable existence-probe
   idiom (`!stat(path, &buf)` — chibi's find-module loop). stat()/fstat()
   answer existence honestly (File/Directory.Exists; open slot) and fill the
   common fields (st_size, st_mode file-type bits, st_mtime). Fields beyond
   those are zero. */

#ifndef _OFF_T_DEFINED
#define _OFF_T_DEFINED
typedef long off_t;
#endif

struct stat {
    unsigned long st_dev;
    unsigned long st_ino;
    unsigned int  st_mode;
    unsigned long st_nlink;
    unsigned int  st_uid;
    unsigned int  st_gid;
    off_t         st_size;
    long          st_atime;
    long          st_mtime;
    long          st_ctime;
};

#define S_IFMT  0170000
#define S_IFDIR 0040000
#define S_IFREG 0100000

#define S_ISDIR(m) (((m) & S_IFMT) == S_IFDIR)
#define S_ISREG(m) (((m) & S_IFMT) == S_IFREG)

int stat(const char *path, struct stat *buf);
int fstat(int fd, struct stat *buf);

#endif /* _SYS_STAT_H */

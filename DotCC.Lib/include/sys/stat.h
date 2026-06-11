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
    /* Appended after st_ctime so the offsets the runtime fills (PosixLib
       FillStat: st_mode/st_size/st_*time) stay put. Reported as 0. */
    unsigned long st_rdev;
    long          st_blksize;
    long          st_blocks;
};

#define S_IFMT   0170000
#define S_IFSOCK 0140000
#define S_IFLNK  0120000
#define S_IFREG  0100000
#define S_IFBLK  0060000
#define S_IFDIR  0040000
#define S_IFCHR  0020000
#define S_IFIFO  0010000

#define S_ISDIR(m)  (((m) & S_IFMT) == S_IFDIR)
#define S_ISREG(m)  (((m) & S_IFMT) == S_IFREG)
#define S_ISSOCK(m) (((m) & S_IFMT) == S_IFSOCK)
#define S_ISLNK(m)  (((m) & S_IFMT) == S_IFLNK)
#define S_ISBLK(m)  (((m) & S_IFMT) == S_IFBLK)
#define S_ISCHR(m)  (((m) & S_IFMT) == S_IFCHR)
#define S_ISFIFO(m) (((m) & S_IFMT) == S_IFIFO)

/* Permission + set-id/sticky bits. */
#define S_ISUID 04000
#define S_ISGID 02000
#define S_ISVTX 01000
#define S_IRWXU 00700
#define S_IRUSR 00400
#define S_IWUSR 00200
#define S_IXUSR 00100
#define S_IRWXG 00070
#define S_IRGRP 00040
#define S_IWGRP 00020
#define S_IXGRP 00010
#define S_IRWXO 00007
#define S_IROTH 00004
#define S_IWOTH 00002
#define S_IXOTH 00001

int stat(const char *path, struct stat *buf);
int fstat(int fd, struct stat *buf);
int lstat(const char *path, struct stat *buf);
int mkdir(const char *path, unsigned int mode);
int mkfifo(const char *path, unsigned int mode);
int chmod(const char *path, unsigned int mode);

#endif /* _SYS_STAT_H */

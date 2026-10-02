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

/* The program's arguments: their count into *argc and the bytes they take, each with its NUL,
   into *argv_buf_size. */
int __wasi_args_sizes_get(unsigned *argc, unsigned *argv_buf_size);

/* The arguments into argv_buf, one after another, each with its NUL, and their addresses into
   argv, 4 bytes apart (wasm32's pointers). */
int __wasi_args_get(unsigned *argv, char *argv_buf);

/* The kinds of file WASI reports (a filestat's filetype, a dirent's d_type). */
#define __WASI_FILETYPE_UNKNOWN          0
#define __WASI_FILETYPE_BLOCK_DEVICE     1
#define __WASI_FILETYPE_CHARACTER_DEVICE 2
#define __WASI_FILETYPE_DIRECTORY        3
#define __WASI_FILETYPE_REGULAR_FILE     4
#define __WASI_FILETYPE_SOCKET_DGRAM     5
#define __WASI_FILETYPE_SOCKET_STREAM    6
#define __WASI_FILETYPE_SYMBOLIC_LINK    7

/* A path call follows a symbolic link at the path's end with this lookup flag. */
#define __WASI_LOOKUP_SYMLINK_FOLLOW 1

/* path_open's oflags and a descriptor's fdflags. */
#define __WASI_OFLAGS_CREAT     1
#define __WASI_OFLAGS_DIRECTORY 2
#define __WASI_OFLAGS_EXCL      4
#define __WASI_OFLAGS_TRUNC     8
#define __WASI_FDFLAGS_APPEND   1
#define __WASI_FDFLAGS_DSYNC    2
#define __WASI_FDFLAGS_NONBLOCK 4
#define __WASI_FDFLAGS_RSYNC    8
#define __WASI_FDFLAGS_SYNC     16

/* Rights: every one of WASI preview1's 30, and those the libc looks at. */
#define __WASI_RIGHTS_ALL      0x3fffffffUL
#define __WASI_RIGHTS_FD_READ  (1UL << 1)
#define __WASI_RIGHTS_FD_SEEK  (1UL << 2)
#define __WASI_RIGHTS_FD_TELL  (1UL << 5)
#define __WASI_RIGHTS_FD_WRITE (1UL << 6)

/* Which of a file's times a set_times call sets, to the given time or to now. */
#define __WASI_FSTFLAGS_ATIM     1
#define __WASI_FSTFLAGS_ATIM_NOW 2
#define __WASI_FSTFLAGS_MTIM     4
#define __WASI_FSTFLAGS_MTIM_NOW 8

/* WASI's structures, laid out as WASI has them; dotcc's LP64 layout of these declarations is
   the same (no pointer in any of them). */
struct __wasi_prestat        /* a preopened directory: tag 0, and its name's length */
{
    unsigned char tag;
    unsigned pr_name_len;
};
struct __wasi_fdstat         /* a descriptor's kind, flags and rights (24 bytes) */
{
    unsigned char fs_filetype;
    unsigned short fs_flags;
    unsigned long fs_rights_base;
    unsigned long fs_rights_inheriting;
};
struct __wasi_filestat       /* a file's attributes, times in nanoseconds (64 bytes) */
{
    unsigned long dev;
    unsigned long ino;
    unsigned char filetype;
    unsigned long nlink;
    unsigned long size;
    unsigned long atim;
    unsigned long mtim;
    unsigned long ctim;
};
struct __wasi_dirent         /* a directory entry's header (24 bytes); its name follows it */
{
    unsigned long d_next;
    unsigned long d_ino;
    unsigned d_namlen;
    unsigned char d_type;
};
struct __wasi_subscription   /* what poll_oneoff waits for: here only a clock (48 bytes) */
{
    unsigned long userdata;
    unsigned char tag;       /* 0: a clock */
    struct
    {
        unsigned id;
        unsigned long timeout;
        unsigned long precision;
        unsigned short flags;   /* 1: timeout is absolute */
    } clock;
};
struct __wasi_event          /* what poll_oneoff saw happen (32 bytes) */
{
    unsigned long userdata;
    unsigned short error;
    unsigned char type;
    unsigned long nbytes;
    unsigned short flags;
};

/* The environment's "NAME=value" strings, as args_sizes_get and args_get give the arguments. */
int __wasi_environ_sizes_get(unsigned *count, unsigned *buf_size);
int __wasi_environ_get(unsigned *environ, char *buf);

/* A preopened directory's descriptor fd: its kind and name's length into *buf (EBADF past the
   last), and the name (no NUL) into path. */
int __wasi_fd_prestat_get(int fd, struct __wasi_prestat *buf);
int __wasi_fd_prestat_dir_name(int fd, char *path, unsigned path_len);

/* Open path (path_len bytes, relative to directory fd) as a new descriptor, into *opened. */
int __wasi_path_open(int fd, unsigned dirflags, const char *path, unsigned path_len, unsigned oflags,
                     unsigned long rights_base, unsigned long rights_inheriting, unsigned fdflags, int *opened);

int __wasi_fd_fdstat_get(int fd, struct __wasi_fdstat *buf);
int __wasi_fd_fdstat_set_flags(int fd, unsigned flags);
int __wasi_fd_filestat_get(int fd, struct __wasi_filestat *buf);
int __wasi_fd_filestat_set_size(int fd, unsigned long size);
int __wasi_fd_filestat_set_times(int fd, unsigned long atim, unsigned long mtim, unsigned fst_flags);
int __wasi_fd_sync(int fd);
int __wasi_fd_tell(int fd, unsigned long *offset);

/* Directory fd's entries from the one at cookie (0: the first), as many as fit in buf, the
   last perhaps cut off; the bytes written into *bufused (fewer than buf_len: no more follow). */
int __wasi_fd_readdir(int fd, void *buf, unsigned buf_len, unsigned long cookie, unsigned *bufused);

int __wasi_path_filestat_get(int fd, unsigned flags, const char *path, unsigned path_len, struct __wasi_filestat *buf);
int __wasi_path_filestat_set_times(int fd, unsigned flags, const char *path, unsigned path_len,
                                   unsigned long atim, unsigned long mtim, unsigned fst_flags);
int __wasi_path_create_directory(int fd, const char *path, unsigned path_len);
int __wasi_path_remove_directory(int fd, const char *path, unsigned path_len);
int __wasi_path_unlink_file(int fd, const char *path, unsigned path_len);
int __wasi_path_rename(int fd, const char *old_path, unsigned old_len, int new_fd, const char *new_path, unsigned new_len);
int __wasi_path_link(int old_fd, unsigned old_flags, const char *old_path, unsigned old_len,
                     int new_fd, const char *new_path, unsigned new_len);
int __wasi_path_symlink(const char *old_path, unsigned old_len, int fd, const char *new_path, unsigned new_len);
int __wasi_path_readlink(int fd, const char *path, unsigned path_len, char *buf, unsigned buf_len, unsigned *bufused);

/* A clock's resolution, in nanoseconds, into *resolution. */
int __wasi_clock_res_get(unsigned clock_id, unsigned long *resolution);

/* Wait for the nsubscriptions events at in; what happened into out, their count into *nevents. */
int __wasi_poll_oneoff(const struct __wasi_subscription *in, struct __wasi_event *out, unsigned nsubscriptions, unsigned *nevents);

/* wasi-threads (imported from module "wasi" as "thread-spawn"): run wasi_thread_start(tid, arg)
   in a new thread's instance of the module; the new thread's id, or a negative error. */
int __wasi_thread_spawn(void *start_arg);

#endif

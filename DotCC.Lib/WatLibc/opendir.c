/* opendir: a directory stream over WASI's fd_readdir (POSIX). readdir, closedir and rewinddir,
   here too, read it, close it and start it over. The stream reads entries a buffer at a time;
   WASI cuts off an entry that does not fit, which is read again from its cookie, into a buffer
   that holds it. WASI's entries include "." and "..", as POSIX's do. The wat target's libc (WatLibc), compiled with the program.
   dotcc-libc: also defines readdir closedir rewinddir */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <posix_impl.h>

/* What a DIR * points at. */
struct stream
{
    int fd;
    int eof;                 /* the last fd_readdir did not fill the buffer: nothing follows it */
    unsigned long cookie;    /* where the entry after the last one returned starts */
    unsigned size;           /* the buffer's size */
    unsigned used;           /* the bytes it holds */
    unsigned pos;            /* where in it the next entry starts */
    char *buf;
    struct dirent entry;
};

DIR *opendir(const char *name)
{
    struct stream *d;
    int fd = open(name, O_RDONLY | O_DIRECTORY);
    if (fd < 0)
    {
        return 0;
    }
    d = calloc(1, sizeof *d);
    if (d)
    {
        d->size = 4096;
        d->buf = malloc(d->size);
    }
    if (!d || !d->buf)
    {
        free(d);
        close(fd);
        errno = ENOMEM;
        return 0;
    }
    d->fd = fd;
    return (DIR *)d;
}

static unsigned char dtype(unsigned char t)
{
    switch (t)
    {
    case __WASI_FILETYPE_BLOCK_DEVICE: return DT_BLK;
    case __WASI_FILETYPE_CHARACTER_DEVICE: return DT_CHR;
    case __WASI_FILETYPE_DIRECTORY: return DT_DIR;
    case __WASI_FILETYPE_REGULAR_FILE: return DT_REG;
    case __WASI_FILETYPE_SOCKET_DGRAM:
    case __WASI_FILETYPE_SOCKET_STREAM: return DT_SOCK;
    case __WASI_FILETYPE_SYMBOLIC_LINK: return DT_LNK;
    default: return DT_UNKNOWN;
    }
}

struct dirent *readdir(DIR *dirp)
{
    struct stream *d = (struct stream *)dirp;
    for (;;)
    {
        if (d->used - d->pos >= sizeof(struct __wasi_dirent))
        {
            struct __wasi_dirent ent;
            unsigned total;
            memcpy(&ent, d->buf + d->pos, sizeof ent);
            total = sizeof ent + ent.d_namlen;
            if (d->used - d->pos >= total)
            {
                const char *name = d->buf + d->pos + sizeof ent;
                d->pos += total;
                d->cookie = ent.d_next;
                /* A name too long for d_name is passed over. */
                if (ent.d_namlen >= sizeof d->entry.d_name)
                {
                    continue;
                }
                memcpy(d->entry.d_name, name, ent.d_namlen);
                d->entry.d_name[ent.d_namlen] = 0;
                d->entry.d_ino = ent.d_ino;
                d->entry.d_type = dtype(ent.d_type);
                return &d->entry;
            }
            if (total > d->size)
            {
                char *bigger = realloc(d->buf, total);
                if (!bigger)
                {
                    errno = ENOMEM;
                    return 0;
                }
                d->buf = bigger;
                d->size = total;
            }
        }
        if (d->eof)
        {
            return 0;
        }
        {
            unsigned got;
            int err = __wasi_fd_readdir(d->fd, d->buf, d->size, d->cookie, &got);
            if (err)
            {
                errno = __wasi_errno(err);
                return 0;
            }
            d->used = got;
            d->pos = 0;
            if (got < d->size)
            {
                d->eof = 1;
            }
        }
    }
}

int closedir(DIR *dirp)
{
    struct stream *d = (struct stream *)dirp;
    int r = close(d->fd);
    free(d->buf);
    free(d);
    return r;
}

void rewinddir(DIR *dirp)
{
    struct stream *d = (struct stream *)dirp;
    d->cookie = 0;
    d->used = 0;
    d->pos = 0;
    d->eof = 0;
}

/* __wasi_to_stat: a struct stat from a WASI filestat (see posix_impl.h). The wat target's libc (WatLibc), compiled with the program. */
#include <string.h>
#include <posix_impl.h>

void __wasi_to_stat(const struct __wasi_filestat *fs, struct stat *st)
{
    unsigned mode;
    switch (fs->filetype)
    {
    case __WASI_FILETYPE_BLOCK_DEVICE: mode = S_IFBLK | 0644; break;
    case __WASI_FILETYPE_CHARACTER_DEVICE: mode = S_IFCHR | 0644; break;
    case __WASI_FILETYPE_DIRECTORY: mode = S_IFDIR | 0755; break;
    case __WASI_FILETYPE_REGULAR_FILE: mode = S_IFREG | 0644; break;
    case __WASI_FILETYPE_SOCKET_DGRAM:
    case __WASI_FILETYPE_SOCKET_STREAM: mode = S_IFSOCK | 0644; break;
    case __WASI_FILETYPE_SYMBOLIC_LINK: mode = S_IFLNK | 0777; break;
    default: mode = 0; break;
    }
    memset(st, 0, sizeof *st);
    st->st_dev = fs->dev;
    st->st_ino = fs->ino;
    st->st_mode = mode;
    st->st_nlink = fs->nlink;
    st->st_size = (off_t)fs->size;
    st->st_atime = (long)(fs->atim / 1000000000);
    st->st_mtime = (long)(fs->mtim / 1000000000);
    st->st_ctime = (long)(fs->ctim / 1000000000);
    st->st_blksize = 4096;
    st->st_blocks = (long)((fs->size + 511) / 512);
}
